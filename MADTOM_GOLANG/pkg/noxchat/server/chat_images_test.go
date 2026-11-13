package server

import (
	"encoding/json"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
	"google.golang.org/protobuf/encoding/protojson"
)

// The chat image RPCs are passthroughs: requests are JSON-encoded with encoding/json
// (bridge.CallRpc) and replies are decoded with protojson. These tests pin the wire
// shapes against the NoxChat Python handlers in src/control/rpc_handlers.py.

func TestChatImageRequestsUsePythonFieldNames(t *testing.T) {
	raw, err := json.Marshal(&madtomv1.NoxSweepChatImagesRequest{
		ChatId: -100123, Uuids: []string{"a", "b"}, OlderThanDays: 7.5, KeepRecent: 3, DryRun: true,
	})
	if err != nil {
		t.Fatal(err)
	}
	var got map[string]any
	if err := json.Unmarshal(raw, &got); err != nil {
		t.Fatal(err)
	}
	for _, key := range []string{"chat_id", "uuids", "older_than_days", "keep_recent", "dry_run"} {
		if _, ok := got[key]; !ok {
			t.Fatalf("sweep request missing %q: %s", key, raw)
		}
	}

	raw, _ = json.Marshal(&madtomv1.NoxGetChatImageRequest{ChatId: 42, Uuid: "u1", MaxDimension: 256})
	got = map[string]any{}
	_ = json.Unmarshal(raw, &got)
	if got["chat_id"] != float64(42) || got["uuid"] != "u1" || got["max_dimension"] != float64(256) {
		t.Fatalf("unexpected get_chat_image payload: %s", raw)
	}
}

func TestChatImageResponsesDecodePythonPayloads(t *testing.T) {
	opts := protojson.UnmarshalOptions{DiscardUnknown: true}

	img := `{"uuid":"u1","handle":"img_1","chat_id":-100123,"origin":"generated","width":1024,"height":768,` +
		`"bytes":204800,"mime_type":"image/png","source_message_id":77,"job_id":"j1","description":"a cat",` +
		`"created_at":"2026-11-01T10:00:00+00:00","last_used_at":""}`

	var list madtomv1.NoxListChatImagesResponse
	if err := opts.Unmarshal([]byte(`{"images":[`+img+`]}`), &list); err != nil {
		t.Fatal(err)
	}
	if len(list.Images) != 1 || list.Images[0].GetBytes() != 204800 || list.Images[0].GetChatId() != -100123 || list.Images[0].GetHandle() != "img_1" {
		t.Fatalf("unexpected list decode: %v", list.Images)
	}

	var one madtomv1.NoxGetChatImageResponse
	if err := opts.Unmarshal([]byte(`{"image":`+img+`,"mime_type":"image/jpeg","image_bytes":"/9j/AA=="}`), &one); err != nil {
		t.Fatal(err)
	}
	if one.GetMimeType() != "image/jpeg" || len(one.GetImageBytes()) != 4 || one.GetImage().GetUuid() != "u1" {
		t.Fatalf("unexpected get decode: %v", &one)
	}

	var stats madtomv1.NoxImageStorageStatsResponse
	if err := opts.Unmarshal([]byte(`{"total_bytes":1048576,"image_count":5,"max_total_bytes":2147483648,"max_bytes_per_chat":209715200,`+
		`"chats":[{"chat_id":7,"image_count":5,"total_bytes":1048576,"oldest":"2026-10-01","newest":"2026-11-01"}]}`), &stats); err != nil {
		t.Fatal(err)
	}
	if stats.GetMaxTotalBytes() != 2147483648 || len(stats.GetChats()) != 1 || stats.GetChats()[0].GetImageCount() != 5 {
		t.Fatalf("unexpected stats decode: %v", &stats)
	}

	var sweep madtomv1.NoxSweepChatImagesResponse
	if err := opts.Unmarshal([]byte(`{"deleted_count":3,"freed_bytes":12345}`), &sweep); err != nil {
		t.Fatal(err)
	}
	if sweep.GetDeletedCount() != 3 || sweep.GetFreedBytes() != 12345 {
		t.Fatalf("unexpected sweep decode: %v", &sweep)
	}
}
