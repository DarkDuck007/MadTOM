package server

import (
	"context"
	"encoding/json"
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// The tips RPCs are passthroughs to NoxChat (src/control/rpc_handlers.py): requests are
// JSON-encoded with encoding/json and replies are decoded into the generated structs.
// These tests pin the wire shapes both ways.

func tipKeys(t *testing.T, msg any) map[string]any {
	t.Helper()
	raw, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal failed: %v", err)
	}
	var got map[string]any
	if err := json.Unmarshal(raw, &got); err != nil {
		t.Fatalf("unmarshal failed: %v", err)
	}
	return got
}

func TestTipRequestsUsePythonFieldNames(t *testing.T) {
	list := tipKeys(t, &madtomv1.NoxTipListRequest{NodeId: "n", ScopeKind: "chat", ScopeValue: "-100456",
		Status: "proposed", State: "ready", Author: "nox", Text: "meme", Limit: 50, Offset: 10})
	for _, key := range []string{"node_id", "scope_kind", "scope_value", "status", "state", "author", "text", "limit", "offset"} {
		if _, ok := list[key]; !ok {
			t.Fatalf("list request missing %q: %v", key, list)
		}
	}

	add := tipKeys(t, &madtomv1.NoxAddTipsRequest{NodeId: "n", RawText: "a\n\nb", ScopeKind: "persona", ScopeValue: "minthara"})
	if add["raw_text"] != "a\n\nb" || add["scope_kind"] != "persona" || add["scope_value"] != "minthara" {
		t.Fatalf("unexpected add payload: %v", add)
	}

	upd := tipKeys(t, &madtomv1.NoxUpdateTipRequest{NodeId: "n", Id: "T-1a2b3c4d", SetText: true, Text: "x",
		SetTrigger: true, Trigger: "when y", SetEnabled: true, Enabled: true, Status: "active",
		SetScope: true, ScopeKind: "global"})
	for _, key := range []string{"id", "set_text", "text", "set_trigger", "trigger", "set_enabled", "enabled", "status", "set_scope", "scope_kind"} {
		if _, ok := upd[key]; !ok {
			t.Fatalf("update request missing %q: %v", key, upd)
		}
	}
	// Zero values are dropped on the wire: the Python side must treat a missing flag as false.
	off := tipKeys(t, &madtomv1.NoxUpdateTipRequest{Id: "T-1a2b3c4d", SetEnabled: true, Enabled: false})
	if _, ok := off["enabled"]; ok {
		t.Fatalf("expected a false bool to be omitted: %v", off)
	}
	if off["set_enabled"] != true {
		t.Fatalf("set_enabled missing: %v", off)
	}

	del := tipKeys(t, &madtomv1.NoxDeleteTipsRequest{NodeId: "n", Ids: []string{"T-1", "T-2"}})
	if ids, ok := del["ids"].([]any); !ok || len(ids) != 2 {
		t.Fatalf("unexpected delete payload: %v", del)
	}

	test := tipKeys(t, &madtomv1.NoxTestTipsRequest{NodeId: "n", Text: "hello", ChatId: -100456, PersonaKey: "nox"})
	if test["chat_id"] != float64(-100456) || test["persona_key"] != "nox" || test["text"] != "hello" {
		t.Fatalf("unexpected test payload: %v", test)
	}

	search := tipKeys(t, &madtomv1.NoxTipSearchRequest{NodeId: "n", Query: "news", ScopeKind: "global", Limit: 5})
	if search["query"] != "news" || search["limit"] != float64(5) {
		t.Fatalf("unexpected search payload: %v", search)
	}
}

func TestTipRepliesDecodeFromPythonShapes(t *testing.T) {
	item := `{"id":"T-1a2b3c4d","text":"Use pillow","trigger":"someone asks for a meme","scope_kind":"chat",
		"scope_value":"-100456","author":"nox","status":"proposed","enabled":true,"state":"failed","error":"boom",
		"use_count":7,"last_used_at_unix_sec":1763000000,"created_at_unix_sec":1762000000,
		"updated_at_unix_sec":1762500000,"created_chat_id":-100456}`

	var list madtomv1.NoxTipListResponse
	listJSON := `{"tips":[` + item + `],"total_count":12,"queue_size":3,"proposed_count":2,
		"settings":{"enabled":true,"min_relevance":0.45,"max_tips":3,"max_tip_chars":300,"max_trigger_chars":200,
		"max_tips_per_chat":30,"query_messages":4,"repeat_after_turns":12}}`
	if err := json.Unmarshal([]byte(listJSON), &list); err != nil {
		t.Fatalf("list reply: %v", err)
	}
	tip := list.GetTips()[0]
	if tip.GetId() != "T-1a2b3c4d" || tip.GetTrigger() != "someone asks for a meme" || tip.GetScopeKind() != "chat" ||
		tip.GetScopeValue() != "-100456" || tip.GetAuthor() != "nox" || tip.GetStatus() != "proposed" ||
		!tip.GetEnabled() || tip.GetState() != "failed" || tip.GetError() != "boom" || tip.GetUseCount() != 7 ||
		tip.GetLastUsedAtUnixSec() != 1763000000 || tip.GetCreatedAtUnixSec() != 1762000000 ||
		tip.GetUpdatedAtUnixSec() != 1762500000 || tip.GetCreatedChatId() != -100456 {
		t.Fatalf("tip item decoded wrong: %+v", tip)
	}
	if list.GetTotalCount() != 12 || list.GetQueueSize() != 3 || list.GetProposedCount() != 2 {
		t.Fatalf("list counters decoded wrong: %+v", &list)
	}
	st := list.GetSettings()
	if !st.GetEnabled() || st.GetMinRelevance() != 0.45 || st.GetMaxTips() != 3 || st.GetMaxTipChars() != 300 ||
		st.GetMaxTriggerChars() != 200 || st.GetMaxTipsPerChat() != 30 || st.GetQueryMessages() != 4 ||
		st.GetRepeatAfterTurns() != 12 {
		t.Fatalf("settings decoded wrong: %+v", st)
	}

	var add madtomv1.NoxAddTipsResponse
	if err := json.Unmarshal([]byte(`{"added":[`+item+`],"rejected":[{"text":"zzz","reason":"too_long"}],"queue_size":1}`), &add); err != nil {
		t.Fatalf("add reply: %v", err)
	}
	if len(add.GetAdded()) != 1 || add.GetRejected()[0].GetReason() != "too_long" || add.GetRejected()[0].GetText() != "zzz" || add.GetQueueSize() != 1 {
		t.Fatalf("add reply decoded wrong: %+v", &add)
	}

	var mut madtomv1.NoxTipMutationResponse
	if err := json.Unmarshal([]byte(`{"success":true,"message":"","tip":`+item+`,"affected":1,"queue_size":2}`), &mut); err != nil {
		t.Fatalf("mutation reply: %v", err)
	}
	if !mut.GetSuccess() || mut.GetTip().GetId() != "T-1a2b3c4d" || mut.GetAffected() != 1 || mut.GetQueueSize() != 2 {
		t.Fatalf("mutation reply decoded wrong: %+v", &mut)
	}
	var failed madtomv1.NoxTipMutationResponse
	if err := json.Unmarshal([]byte(`{"success":false,"message":"not_found","affected":0,"queue_size":0}`), &failed); err != nil {
		t.Fatalf("failed mutation reply: %v", err)
	}
	if failed.GetSuccess() || failed.GetMessage() != "not_found" || failed.GetTip() != nil {
		t.Fatalf("failed mutation decoded wrong: %+v", &failed)
	}

	var search madtomv1.NoxTipSearchResponse
	if err := json.Unmarshal([]byte(`{"results":[{"tip":`+item+`,"score":0.612}]}`), &search); err != nil {
		t.Fatalf("search reply: %v", err)
	}
	if search.GetResults()[0].GetScore() != 0.612 || search.GetResults()[0].GetTip().GetText() != "Use pillow" {
		t.Fatalf("search reply decoded wrong: %+v", &search)
	}

	var test madtomv1.NoxTestTipsResponse
	testJSON := `{"threshold":0.45,"shown":[{"tip":` + item + `,"score":0.58}],"below_threshold":[{"tip":` + item + `,"score":0.41}]}`
	if err := json.Unmarshal([]byte(testJSON), &test); err != nil {
		t.Fatalf("test reply: %v", err)
	}
	if test.GetThreshold() != 0.45 || len(test.GetShown()) != 1 || test.GetBelowThreshold()[0].GetScore() != 0.41 {
		t.Fatalf("test reply decoded wrong: %+v", &test)
	}
}

type tipsCtx = context.Context

// Compile-time check: the server implements every tips RPC (not just the embedded "unimplemented" stubs).
var _ interface {
	ListTips(ctx tipsCtx, req *madtomv1.NoxTipListRequest) (*madtomv1.NoxTipListResponse, error)
	SearchTips(ctx tipsCtx, req *madtomv1.NoxTipSearchRequest) (*madtomv1.NoxTipSearchResponse, error)
	AddTips(ctx tipsCtx, req *madtomv1.NoxAddTipsRequest) (*madtomv1.NoxAddTipsResponse, error)
	UpdateTip(ctx tipsCtx, req *madtomv1.NoxUpdateTipRequest) (*madtomv1.NoxTipMutationResponse, error)
	DeleteTips(ctx tipsCtx, req *madtomv1.NoxDeleteTipsRequest) (*madtomv1.NoxTipMutationResponse, error)
	TestTips(ctx tipsCtx, req *madtomv1.NoxTestTipsRequest) (*madtomv1.NoxTestTipsResponse, error)
} = (*Server)(nil)
