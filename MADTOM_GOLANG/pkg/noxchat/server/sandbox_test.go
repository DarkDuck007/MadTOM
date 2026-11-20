package server

import (
	"encoding/json"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// The sandbox RPCs are passthroughs to NoxChat (src/control/rpc_handlers.py sandbox_*).
// The JSON keys must match the Python side and contracts.md §2.3/§5.

func decodeInto(t *testing.T, raw string, dst any) {
	t.Helper()
	if err := json.Unmarshal([]byte(raw), dst); err != nil {
		t.Fatalf("decode failed: %v", err)
	}
}

func i64(v int64) *int64 { return &v }
func i32(v int32) *int32 { return &v }

func TestSandboxRequestsUsePythonFieldNames(t *testing.T) {
	act := tipKeys(t, &madtomv1.NoxSandboxVmActionRequest{NodeId: "n", ChatId: i64(-100123), Action: "reset_session", Session: "main"})
	if act["chat_id"] != float64(-100123) || act["action"] != "reset_session" || act["session"] != "main" {
		t.Fatalf("unexpected vm action payload: %v", act)
	}
	// optional chat_id keeps an explicit 0 (Python does int(payload['chat_id']) and would KeyError otherwise)
	zero := tipKeys(t, &madtomv1.NoxSandboxVmActionRequest{ChatId: i64(0), Action: "kill"})
	if v, ok := zero["chat_id"]; !ok || v != float64(0) {
		t.Fatalf("explicit chat_id 0 dropped: %v", zero)
	}

	files := tipKeys(t, &madtomv1.NoxSandboxListFilesRequest{ChatId: i64(5), Path: "/data/notes", Depth: i32(0), Refresh: true})
	if files["path"] != "/data/notes" || files["refresh"] != true {
		t.Fatalf("unexpected list files payload: %v", files)
	}
	if v, ok := files["depth"]; !ok || v != float64(0) {
		t.Fatalf("explicit depth 0 dropped: %v", files)
	}

	prev := tipKeys(t, &madtomv1.NoxSandboxPreviewFileRequest{ChatId: i64(5), Path: "/data/a.pdf", Page: 3})
	if prev["path"] != "/data/a.pdf" || prev["page"] != float64(3) {
		t.Fatalf("unexpected preview payload: %v", prev)
	}

	rest := tipKeys(t, &madtomv1.NoxSandboxRestoreBackupRequest{ChatId: i64(5), BackupId: "20261120T030000Z"})
	if rest["backup_id"] != "20261120T030000Z" {
		t.Fatalf("unexpected restore payload: %v", rest)
	}

	// approve=false is omitted on the wire; Python defaults a missing approve to False.
	rej := tipKeys(t, &madtomv1.NoxSandboxDecideLibraryRequest{Ids: []int64{3, 4}, Approve: false, Note: "no", DecidedBy: "admin"})
	if _, ok := rej["approve"]; ok {
		t.Fatalf("expected approve=false to be omitted: %v", rej)
	}
	if ids, ok := rej["ids"].([]any); !ok || len(ids) != 2 || rej["note"] != "no" || rej["decided_by"] != "admin" {
		t.Fatalf("unexpected decide payload: %v", rej)
	}
	ok := tipKeys(t, &madtomv1.NoxSandboxDecideLibraryRequest{Ids: []int64{3}, Approve: true})
	if ok["approve"] != true {
		t.Fatalf("approve=true missing: %v", ok)
	}

	lib := tipKeys(t, &madtomv1.NoxSandboxListLibraryRequestsRequest{Status: "pending"})
	if _, has := lib["chat_id"]; has {
		t.Fatalf("unset optional chat_id must be omitted (means all chats): %v", lib)
	}
	runs := tipKeys(t, &madtomv1.NoxSandboxListRunsRequest{ChatId: i64(7), Limit: 20, Offset: 40})
	if runs["chat_id"] != float64(7) || runs["limit"] != float64(20) || runs["offset"] != float64(40) {
		t.Fatalf("unexpected runs payload: %v", runs)
	}
}

func TestSandboxRepliesDecodeFromPythonShapes(t *testing.T) {
	var vms madtomv1.NoxSandboxListVmsResponse
	decodeInto(t, `{"vms":[{"chat_id":-100123,"chat_title":"Test","kind":"chat","state":"warm","image_version":"2026.11.20-1",
		"mem_mb":1024,"vcpus":2,"pid":4242,"since":"2026-11-20T10:00:00Z","last_used":"2026-11-20T10:01:00Z",
		"idle_deadline":"2026-11-20T10:06:00Z","session_deadline":"2026-11-20T10:30:00Z","disk_used_bytes":1048576,
		"disk_quota_bytes":2147483648,"snapshot_bytes":0,"backups":2,"cpu_seconds_today":12.5,"cpu_budget_seconds":1800,
		"sessions":[{"name":"main","busy":true,"started":"2026-11-20T10:00:01Z"},{"name":"s2","busy":false,"started":"2026-11-20T10:00:02Z"}],
		"jobs":[{"job_id":"j-1","state":"running","started":"2026-11-20T10:00:03Z"}]}],
		"pools":{"temp":{"ready":2,"target":2},"pre":{"ready":1,"target":4}},
		"totals":{"running":1,"guest_mem_mb":1024,"max_vms":8,"max_guest_mem_mb":8192},
		"host":{"state_dir_used_percent":41.5}}`, &vms)
	if len(vms.Vms) != 1 {
		t.Fatalf("vms not decoded: %+v", &vms)
	}
	v := vms.Vms[0]
	if v.ChatId != -100123 || v.ChatTitle != "Test" || v.State != "warm" || v.DiskQuotaBytes != 2147483648 || v.CpuSecondsToday != 12.5 || v.Backups != 2 {
		t.Fatalf("vm fields wrong: %+v", v)
	}
	if len(v.Sessions) != 2 || !v.Sessions[0].Busy || v.Sessions[1].Name != "s2" || len(v.Jobs) != 1 || v.Jobs[0].JobId != "j-1" {
		t.Fatalf("nested sessions/jobs wrong: %+v", v)
	}
	if vms.Pools["pre"].GetTarget() != 4 || vms.Totals.GetMaxGuestMemMb() != 8192 || vms.Host.GetStateDirUsedPercent() != 41.5 {
		t.Fatalf("pools/totals/host wrong: %+v", &vms)
	}

	var files madtomv1.NoxSandboxListFilesResponse
	decodeInto(t, `{"entries":[{"path":"/data/notes/a.md","type":"file","size":12,"mtime":1795150414}],"as_of":"2026-11-20T10:00:00Z","from_cache":true}`, &files)
	if len(files.Entries) != 1 || files.Entries[0].Mtime != 1795150414 || !files.FromCache {
		t.Fatalf("files wrong: %+v", &files)
	}

	var prev madtomv1.NoxSandboxPreviewFileResponse
	decodeInto(t, `{"png_b64":"iVBORw0KGgo=","width":200,"height":60,"pages":1,"kind":"text"}`, &prev)
	if prev.PngB64 != "iVBORw0KGgo=" || prev.Width != 200 || prev.Kind != "text" {
		t.Fatalf("preview wrong: %+v", &prev)
	}

	var img madtomv1.NoxSandboxImageStatusResponse
	decodeInto(t, `{"current_version":"2026.11.20-1","building":false,"last_build":{"version":"2026.11.20-1","ok":true,"finished":"x","log_tail":"done"},
		"packages":{"pip":["pandas"],"apt":["pandoc"]},"pending_packages":{"pip":["sympy"],"apt":[]},"versions_in_use":{"2026.11.20-1":3}}`, &img)
	if img.LastBuild.GetLogTail() != "done" || img.PendingPackages.GetPip()[0] != "sympy" || img.VersionsInUse["2026.11.20-1"] != 3 {
		t.Fatalf("image status wrong: %+v", &img)
	}

	var lib madtomv1.NoxSandboxListLibraryRequestsResponse
	decodeInto(t, `{"requests":[{"id":3,"chat_id":-100123,"requester_user_id":null,"manager":"pip","package":"sympy","reason":"math",
		"status":"pending","decided_by":null,"decided_at":null,"decision_note":null,"image_version":null,"notified":0,"created_at":"2026-11-20 10:00:00"}]}`, &lib)
	if len(lib.Requests) != 1 || lib.Requests[0].Package != "sympy" || lib.Requests[0].RequesterUserId != nil {
		t.Fatalf("library rows wrong: %+v", &lib)
	}

	var runs madtomv1.NoxSandboxListRunsResponse
	decodeInto(t, `{"runs":[{"id":1,"chat_id":5,"requester_user_id":77,"vm":"chat","kind":"python","session":"main","code_preview":"x",
		"exit_code":0,"error":null,"duration_ms":12,"inputs":0,"exports":1,"background_job":null,"created_at":"2026-11-20 10:00:00"}],"total":1,"limit":50,"offset":0}`, &runs)
	if len(runs.Runs) != 1 || runs.Runs[0].GetRequesterUserId() != 77 || runs.Runs[0].ExitCode == nil || *runs.Runs[0].ExitCode != 0 {
		t.Fatalf("runs wrong: %+v", &runs)
	}

	var e madtomv1.NoxSandboxVmActionResponse
	decodeInto(t, `{"error":"sandbox unavailable"}`, &e)
	if e.Error != "sandbox unavailable" || e.Ok {
		t.Fatalf("error passthrough wrong: %+v", &e)
	}
}

func TestBudgetAndExecutorFieldsDecode(t *testing.T) {
	var vms madtomv1.NoxSandboxListVmsResponse
	decodeInto(t, `{"vms":[{"chat_id":-5,"kind":"chat","state":"cold","cpu_seconds_today":3599.5,"cpu_budget_seconds":3600,
		"cpu_resets_at":"2026-11-21T00:00:00+09:00","snapshot_quota_bytes":2147483648,"sessions":[],"jobs":[]}]}`, &vms)
	if v := vms.Vms[0]; v.CpuResetsAt != "2026-11-21T00:00:00+09:00" || v.SnapshotQuotaBytes != 2147483648 || v.CpuSecondsToday != 3599.5 {
		t.Fatalf("budget fields wrong: %+v", v)
	}

	// GetActiveJobs decodes the Python reply with encoding/json (server.go); mirror that exactly.
	var jobs madtomv1.NoxActiveJobsResponse
	payload := `{"active_jobs":[{"job_id":"D-1","job_type":"doc_studio","executor":"vm","chat_id":-5,"status":"running",
		"progress":0.5,"details":"PDF \"x\" (vm) — rendering","elapsed_ms":1200}],
		"doc_gen":{"executor":"auto","copy_to_sandbox":true,"sandbox":{"keep_jobs":5,"sync_max_file_bytes":52428800,"exec_margin_s":15,"lock_wait_s":60}}}`
	if err := json.Unmarshal([]byte(payload), &jobs); err != nil {
		t.Fatal(err)
	}
	if len(jobs.ActiveJobs) != 1 || jobs.ActiveJobs[0].Executor != "vm" || jobs.ActiveJobs[0].Progress != 0.5 {
		t.Fatalf("job wrong: %+v", &jobs)
	}
	dg := jobs.GetDocGen()
	if dg.GetExecutor() != "auto" || !dg.GetCopyToSandbox() || dg.GetSandbox().GetKeepJobs() != 5 ||
		dg.GetSandbox().GetSyncMaxFileBytes() != 52428800 || dg.GetSandbox().GetLockWaitS() != 60 {
		t.Fatalf("doc_gen wrong: %+v", dg)
	}
}
