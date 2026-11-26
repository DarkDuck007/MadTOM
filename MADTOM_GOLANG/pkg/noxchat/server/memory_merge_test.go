package server

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// list_memory_merges / memory_merge_action are passthroughs to NoxChat (src/control/rpc_handlers.py,
// src/memory/consolidation.py); the JSON keys must match the Python side.

func TestMemoryMergeRequestsUsePythonFieldNames(t *testing.T) {
	act := tipKeys(t, &madtomv1.NoxMemoryMergeActionRequest{Action: "apply", Ids: []int64{4, 5}, EditedText: "a\nb", ChatId: -100})
	ids, ok := act["ids"].([]any)
	if !ok || len(ids) != 2 || act["action"] != "apply" || act["edited_text"] != "a\nb" || act["chat_id"] != float64(-100) {
		t.Fatalf("unexpected action payload: %v", act)
	}
	all := tipKeys(t, &madtomv1.NoxMemoryMergeListRequest{Status: "pending"})
	if _, has := all["chat_id"]; has {
		t.Fatalf("unset optional chat_id must be omitted (means all chats): %v", all)
	}
	one := tipKeys(t, &madtomv1.NoxMemoryMergeListRequest{ChatId: i64(-1003206381698)})
	if one["chat_id"] != float64(-1003206381698) {
		t.Fatalf("chat_id missing: %v", one)
	}
}

func TestMemoryMergeRepliesDecodeFromPythonShapes(t *testing.T) {
	var list madtomv1.NoxMemoryMergeListResponse
	decodeInto(t, `{"proposals":[{"id":3,"chat_id":-100,"entity_key":"user:42","subject":"@lena","kind":"merge",
		"sources":[{"id":"aa","content":"x","created_at":"2026-11-20"}],"proposed":"a\nb","dropped":["c"],
		"status":"pending","error":"","result_memory_id":"","created_at":"2026-11-26T10:00:00+00:00","decided_at":""}],
		"jobs":[{"chat_id":-100,"running":true,"total":5,"done":2,"failed":0,"started_at":"t","finished_at":"","error":""}]}`, &list)
	p := list.Proposals[0]
	if p.Id != 3 || p.Subject != "@lena" || len(p.Sources) != 1 || p.Sources[0].Id != "aa" || p.Dropped[0] != "c" {
		t.Fatalf("unexpected proposal: %v", p)
	}
	if j := list.Jobs[0]; !j.Running || j.Total != 5 || j.Done != 2 {
		t.Fatalf("unexpected job: %v", j)
	}
	var act madtomv1.NoxMemoryMergeActionResponse
	decodeInto(t, `{"ok":false,"message":"Applied 1 of 2","applied_ids":[3],"errors":["#4: stale"]}`, &act)
	if act.Ok || act.AppliedIds[0] != 3 || act.Errors[0] != "#4: stale" {
		t.Fatalf("unexpected action reply: %v", act)
	}
}
