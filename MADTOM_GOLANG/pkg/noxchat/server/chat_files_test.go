package server

import (
	"bytes"
	"encoding/json"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/encoding/protojson"
)

// The chat files RPCs are passthroughs: requests are JSON-encoded with encoding/json
// (bridge.CallRpcContext) and replies are decoded with protojson. These tests pin the wire
// shapes against the NoxChat Python handlers in src/control/rpc_handlers.py.

func TestChatFileRequestsUsePythonFieldNames(t *testing.T) {
	// 1. ListChatFiles
	listReq := &madtomv1.NoxListChatFilesRequest{
		NodeId:     "node-1",
		ChatId:     -100456,
		Limit:      50,
		Offset:     10,
		KindFilter: "pdf",
		Origin:     "telegram",
	}
	raw, err := json.Marshal(listReq)
	if err != nil {
		t.Fatalf("marshal list req failed: %v", err)
	}
	var gotList map[string]any
	if err := json.Unmarshal(raw, &gotList); err != nil {
		t.Fatalf("unmarshal list req failed: %v", err)
	}
	for _, key := range []string{"node_id", "chat_id", "limit", "offset", "kind_filter", "origin"} {
		if _, ok := gotList[key]; !ok {
			t.Fatalf("list request missing %q: %s", key, raw)
		}
	}
	if gotList["chat_id"] != float64(-100456) || gotList["kind_filter"] != "pdf" {
		t.Fatalf("unexpected list payload: %s", raw)
	}

	// 2. GetChatFilePreview
	prevReq := &madtomv1.NoxGetChatFilePreviewRequest{
		NodeId:       "node-1",
		ChatId:       77,
		Uuid:         "DOC-deck1",
		Mode:         "page",
		MaxDimension: 1024,
		Page:         3,
	}
	raw, err = json.Marshal(prevReq)
	if err != nil {
		t.Fatalf("marshal preview req failed: %v", err)
	}
	var gotPrev map[string]any
	if err := json.Unmarshal(raw, &gotPrev); err != nil {
		t.Fatalf("unmarshal preview req failed: %v", err)
	}
	for _, key := range []string{"node_id", "chat_id", "uuid", "mode", "max_dimension", "page"} {
		if _, ok := gotPrev[key]; !ok {
			t.Fatalf("preview request missing %q: %s", key, raw)
		}
	}
	if gotPrev["mode"] != "page" || gotPrev["max_dimension"] != float64(1024) || gotPrev["page"] != float64(3) {
		t.Fatalf("unexpected preview payload: %s", raw)
	}

	// 3. GetChatFileBytes
	bytesReq := &madtomv1.NoxGetChatFileBytesRequest{
		NodeId: "node-1",
		ChatId: 88,
		Uuid:   "FILE-archive1",
	}
	raw, err = json.Marshal(bytesReq)
	if err != nil {
		t.Fatalf("marshal bytes req failed: %v", err)
	}
	var gotBytes map[string]any
	if err := json.Unmarshal(raw, &gotBytes); err != nil {
		t.Fatalf("unmarshal bytes req failed: %v", err)
	}
	for _, key := range []string{"node_id", "chat_id", "uuid"} {
		if _, ok := gotBytes[key]; !ok {
			t.Fatalf("bytes request missing %q: %s", key, raw)
		}
	}
	if gotBytes["uuid"] != "FILE-archive1" {
		t.Fatalf("unexpected bytes payload: %s", raw)
	}

	// 4. GetWorkspaceStorageStats
	statsReq := &madtomv1.NoxWorkspaceStorageStatsRequest{
		NodeId: "node-1",
	}
	raw, err = json.Marshal(statsReq)
	if err != nil {
		t.Fatalf("marshal stats req failed: %v", err)
	}
	var gotStats map[string]any
	if err := json.Unmarshal(raw, &gotStats); err != nil {
		t.Fatalf("unmarshal stats req failed: %v", err)
	}
	if _, ok := gotStats["node_id"]; !ok {
		t.Fatalf("stats request missing node_id: %s", raw)
	}

	// 5. SweepChatFiles
	sweepReq := &madtomv1.NoxSweepChatFilesRequest{
		NodeId:        "node-1",
		ChatId:        -100789,
		Uuids:         []string{"IMG-1", "DOC-2"},
		OlderThanDays: 14.5,
		KeepRecent:    5,
		KindFilter:    "image",
		DryRun:        true,
	}
	raw, err = json.Marshal(sweepReq)
	if err != nil {
		t.Fatalf("marshal sweep req failed: %v", err)
	}
	var gotSweep map[string]any
	if err := json.Unmarshal(raw, &gotSweep); err != nil {
		t.Fatalf("unmarshal sweep req failed: %v", err)
	}
	for _, key := range []string{"node_id", "chat_id", "uuids", "older_than_days", "keep_recent", "kind_filter", "dry_run"} {
		if _, ok := gotSweep[key]; !ok {
			t.Fatalf("sweep request missing %q: %s", key, raw)
		}
	}
	if gotSweep["dry_run"] != true || gotSweep["older_than_days"] != 14.5 || gotSweep["keep_recent"] != float64(5) {
		t.Fatalf("unexpected sweep payload: %s", raw)
	}
}

func TestChatFileResponsesDecodePythonPayloads(t *testing.T) {
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}

	// 1. ListChatFilesResponse with heterogeneous file types
	file1 := `{"uuid":"u-img","handle":"IMG-001","store":"image","kind":"image","role":"","chat_id":-1001,` +
		`"filename":"cat.png","mime_type":"image/png","bytes":102400,"source_bundle_bytes":0,"width":1920,"height":1080,` +
		`"page_count":0,"duration_s":0.0,"has_audio":false,"origin":"generated","source_message_id":100,"job_id":"job-9",` +
		`"description":"A scenic cat","created_at":"2026-11-10T08:00:00Z","last_used_at":"2026-11-11T12:00:00Z","pinned":true}`

	file2 := `{"uuid":"u-vid","handle":"VID-002","store":"image","kind":"video","role":"","chat_id":-1001,` +
		`"filename":"clip.mp4","mime_type":"video/mp4","bytes":5242880,"source_bundle_bytes":0,"width":1280,"height":720,` +
		`"page_count":0,"duration_s":12.5,"has_audio":true,"origin":"telegram","source_message_id":101,"job_id":"",` +
		`"description":"Short animation","created_at":"2026-11-12T09:00:00Z","last_used_at":"","pinned":false}`

	file3 := `{"uuid":"u-doc","handle":"DOC-003","store":"document","kind":"pptx","role":"primary","chat_id":-1001,` +
		`"filename":"deck.pptx","mime_type":"application/vnd.openxmlformats-officedocument.presentationml.presentation",` +
		`"bytes":819200,"source_bundle_bytes":1638400,"width":0,"height":0,"page_count":15,"duration_s":0.0,"has_audio":false,` +
		`"origin":"studio","source_message_id":0,"job_id":"doc-gen-1","description":"Q4 Deck",` +
		`"created_at":"2026-11-13T10:00:00Z","last_used_at":"","pinned":false}`

	var listResp madtomv1.NoxListChatFilesResponse
	listJson := `{"files":[` + file1 + `,` + file2 + `,` + file3 + `],"total_count":3}`
	if err := opts.Unmarshal([]byte(listJson), &listResp); err != nil {
		t.Fatalf("failed to decode list response: %v", err)
	}

	if listResp.GetTotalCount() != 3 || len(listResp.GetFiles()) != 3 {
		t.Fatalf("unexpected list count: total=%d, len=%d", listResp.GetTotalCount(), len(listResp.GetFiles()))
	}
	f1 := listResp.GetFiles()[0]
	if f1.GetHandle() != "IMG-001" || f1.GetKind() != "image" || !f1.GetPinned() || f1.GetWidth() != 1920 {
		t.Fatalf("unexpected file1: %+v", f1)
	}
	f2 := listResp.GetFiles()[1]
	if f2.GetHandle() != "VID-002" || f2.GetKind() != "video" || !f2.GetHasAudio() || f2.GetDurationS() != 12.5 {
		t.Fatalf("unexpected file2: %+v", f2)
	}
	f3 := listResp.GetFiles()[2]
	if f3.GetHandle() != "DOC-003" || f3.GetRole() != "primary" || f3.GetPageCount() != 15 || f3.GetSourceBundleBytes() != 1638400 {
		t.Fatalf("unexpected file3: %+v", f3)
	}

	// 2. GetChatFilePreviewResponse: test base64 preview bytes decoding
	prevJson := `{"file":` + file1 + `,"mime_type":"image/jpeg","preview_bytes":"/9j/4AAQSkZJRg==","text_content":"","page_count":1,"current_page":1,"frame_count":1}`
	var prevResp madtomv1.NoxGetChatFilePreviewResponse
	if err := opts.Unmarshal([]byte(prevJson), &prevResp); err != nil {
		t.Fatalf("failed to decode preview response: %v", err)
	}
	if prevResp.GetMimeType() != "image/jpeg" || len(prevResp.GetPreviewBytes()) == 0 {
		t.Fatalf("preview bytes empty or wrong mime: %s", prevResp.GetMimeType())
	}
	if prevResp.GetFile().GetUuid() != "u-img" {
		t.Fatalf("preview embedded file uuid mismatch: %s", prevResp.GetFile().GetUuid())
	}

	// 2b. Multi-page document preview & text preview
	docPrevJson := `{"file":` + file3 + `,"mime_type":"image/jpeg","preview_bytes":"/9j/4AAQSkZJRg==","text_content":"Slide 3 Notes","page_count":15,"current_page":3,"frame_count":1}`
	var docPrevResp madtomv1.NoxGetChatFilePreviewResponse
	if err := opts.Unmarshal([]byte(docPrevJson), &docPrevResp); err != nil {
		t.Fatalf("failed to decode doc preview: %v", err)
	}
	if docPrevResp.GetPageCount() != 15 || docPrevResp.GetCurrentPage() != 3 || docPrevResp.GetTextContent() != "Slide 3 Notes" {
		t.Fatalf("unexpected doc preview fields: page=%d/%d, text=%q", docPrevResp.GetCurrentPage(), docPrevResp.GetPageCount(), docPrevResp.GetTextContent())
	}

	// 2c. Video NOXANIM1 animated preview
	animPrevJson := `{"file":` + file2 + `,"mime_type":"application/x-noxanim1","preview_bytes":"Tk9YQU5JTTFAAAAA","text_content":"","page_count":0,"current_page":0,"frame_count":24}`
	var animPrevResp madtomv1.NoxGetChatFilePreviewResponse
	if err := opts.Unmarshal([]byte(animPrevJson), &animPrevResp); err != nil {
		t.Fatalf("failed to decode anim preview: %v", err)
	}
	if animPrevResp.GetFrameCount() != 24 || animPrevResp.GetMimeType() != "application/x-noxanim1" {
		t.Fatalf("unexpected anim preview: frames=%d, mime=%s", animPrevResp.GetFrameCount(), animPrevResp.GetMimeType())
	}
	if !bytes.HasPrefix(animPrevResp.GetPreviewBytes(), []byte("NOXANIM1")) {
		t.Fatalf("expected NOXANIM1 magic prefix in decoded bytes, got %q", animPrevResp.GetPreviewBytes())
	}

	// 3. GetChatFileBytesResponse: base64 file bytes
	bytesJson := `{"filename":"export.csv","mime_type":"text/csv","file_bytes":"Y29sMSxjb2wyCjEsMgo=","total_bytes":14}`
	var bytesResp madtomv1.NoxGetChatFileBytesResponse
	if err := opts.Unmarshal([]byte(bytesJson), &bytesResp); err != nil {
		t.Fatalf("failed to decode bytes response: %v", err)
	}
	if bytesResp.GetFilename() != "export.csv" || bytesResp.GetTotalBytes() != 14 {
		t.Fatalf("unexpected bytes header: %s, %d", bytesResp.GetFilename(), bytesResp.GetTotalBytes())
	}
	if string(bytesResp.GetFileBytes()) != "col1,col2\n1,2\n" {
		t.Fatalf("unexpected bytes content: %q", string(bytesResp.GetFileBytes()))
	}

	// 4. GetWorkspaceStorageStatsResponse
	statsJson := `{
		"total_bytes": 104857600,
		"file_count": 42,
		"max_total_bytes": 10737418240,
		"max_bytes_per_chat": 1073741824,
		"image_bytes": 52428800,
		"document_bytes": 31457280,
		"file_bytes": 20971520,
		"chats": [
			{
				"chat_id": -1001,
				"file_count": 25,
				"total_bytes": 62914560,
				"image_count": 15,
				"video_count": 3,
				"document_count": 5,
				"saved_file_count": 2,
				"oldest": "2026-10-01T00:00:00Z",
				"newest": "2026-11-14T00:00:00Z"
			},
			{
				"chat_id": -1002,
				"file_count": 0,
				"total_bytes": 0,
				"image_count": 0,
				"video_count": 0,
				"document_count": 0,
				"saved_file_count": 0,
				"oldest": "",
				"newest": ""
			}
		]
	}`
	var statsResp madtomv1.NoxWorkspaceStorageStatsResponse
	if err := opts.Unmarshal([]byte(statsJson), &statsResp); err != nil {
		t.Fatalf("failed to decode storage stats: %v", err)
	}
	if statsResp.GetTotalBytes() != 104857600 || statsResp.GetFileCount() != 42 || statsResp.GetMaxTotalBytes() != 10737418240 {
		t.Fatalf("unexpected stats totals: total=%d, count=%d", statsResp.GetTotalBytes(), statsResp.GetFileCount())
	}
	if statsResp.GetImageBytes() != 52428800 || statsResp.GetDocumentBytes() != 31457280 || statsResp.GetFileBytes() != 20971520 {
		t.Fatalf("unexpected store breakdown in stats: img=%d, doc=%d, file=%d",
			statsResp.GetImageBytes(), statsResp.GetDocumentBytes(), statsResp.GetFileBytes())
	}
	if len(statsResp.GetChats()) != 2 {
		t.Fatalf("expected 2 chat usages, got %d", len(statsResp.GetChats()))
	}
	c1 := statsResp.GetChats()[0]
	if c1.GetChatId() != -1001 || c1.GetFileCount() != 25 || c1.GetVideoCount() != 3 || c1.GetDocumentCount() != 5 {
		t.Fatalf("unexpected c1 stats: %+v", c1)
	}
	c2 := statsResp.GetChats()[1]
	if c2.GetChatId() != -1002 || c2.GetFileCount() != 0 || c2.GetTotalBytes() != 0 {
		t.Fatalf("unexpected c2 stats: %+v", c2)
	}

	// 5. SweepChatFilesResponse
	sweepJson := `{"deleted_count":7,"freed_bytes":15728640,"protected_job_files":["IMG-active1","FILE-job-in2"]}`
	var sweepResp madtomv1.NoxSweepChatFilesResponse
	if err := opts.Unmarshal([]byte(sweepJson), &sweepResp); err != nil {
		t.Fatalf("failed to decode sweep response: %v", err)
	}
	if sweepResp.GetDeletedCount() != 7 || sweepResp.GetFreedBytes() != 15728640 {
		t.Fatalf("unexpected sweep counts: deleted=%d, freed=%d", sweepResp.GetDeletedCount(), sweepResp.GetFreedBytes())
	}
	if len(sweepResp.GetProtectedJobFiles()) != 2 || sweepResp.GetProtectedJobFiles()[0] != "IMG-active1" {
		t.Fatalf("unexpected protected files: %v", sweepResp.GetProtectedJobFiles())
	}
}
