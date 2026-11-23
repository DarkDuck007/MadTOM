package server

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// Automation RPCs are passthroughs to NoxChat automation_* handlers
// (plans/2026-11-20-preprocessors-crons/contracts.md §8). JSON keys must match the Python side.

func TestAutomationRequestsUsePythonFieldNames(t *testing.T) {
	if all := tipKeys(t, &madtomv1.NoxListPreprocessorsRequest{}); len(all) != 0 {
		t.Fatalf("unset optional chat_id must be omitted (all chats): %v", all)
	}
	one := tipKeys(t, &madtomv1.NoxListPreprocessorsRequest{ChatId: i64(-100)})
	if one["chat_id"] != float64(-100) {
		t.Fatalf("chat filter lost: %v", one)
	}
	act := tipKeys(t, &madtomv1.NoxPreprocessorActionRequest{Id: 7, Action: "reset_state"})
	if act["id"] != float64(7) || act["action"] != "reset_state" {
		t.Fatalf("preprocessor action payload: %v", act)
	}
	logs := tipKeys(t, &madtomv1.NoxListPreprocessorLogsRequest{ChatId: i64(1), PreprocessorId: i64(3), Limit: 50, Offset: 100})
	if logs["chat_id"] != float64(1) || logs["preprocessor_id"] != float64(3) || logs["limit"] != float64(50) || logs["offset"] != float64(100) {
		t.Fatalf("logs payload: %v", logs)
	}
	cron := tipKeys(t, &madtomv1.NoxCronActionRequest{Id: 9, Action: "cancel"})
	if cron["id"] != float64(9) || cron["action"] != "cancel" {
		t.Fatalf("cron action payload: %v", cron)
	}
	if get := tipKeys(t, &madtomv1.NoxGetPreprocessorRequest{Id: 4}); get["id"] != float64(4) {
		t.Fatalf("get payload: %v", get)
	}
}

func TestAutomationRepliesDecodeFromPythonShapes(t *testing.T) {
	var st madtomv1.NoxAutomationStatusResponse
	decodeInto(t, `{"chats":[{"chat_id":-100,"chat_title":"G","preprocessors":3,"enabled_preprocessors":2,"crons":1,
		"say_streak":4,"runs_last_hour":10,"says_last_hour":5,"fires_today":2,"next_fire_at":"2026-11-20T07:00:00Z"}],
		"config_json":"{\"preprocess\":{}}","error":""}`, &st)
	if len(st.Chats) != 1 || st.Chats[0].SayStreak != 4 || st.Chats[0].NextFireAt == "" || st.ConfigJson == "" {
		t.Fatalf("status decode: %+v", st.Chats)
	}

	var pp madtomv1.NoxListPreprocessorsResponse
	decodeInto(t, `{"preprocessors":[{"id":1,"chat_id":-100,"chat_title":"G","name":"greeter","kind":"code",
		"triggers":["message","cron"],"priority":100,"enabled":true,"filter_json":"{\"text_regex\":\"hi\"}",
		"code_bytes":120,"state_bytes":2,"error_count":0,"last_error":"","last_run_at":"","runs":5,
		"created_at":"2026-11-20T06:00:00Z","updated_at":"2026-11-20T06:00:00Z","created_by_user_id":7,
		"expires_at":"","description":"says hi"}],"error":""}`, &pp)
	p := pp.Preprocessors[0]
	if p.Name != "greeter" || len(p.Triggers) != 2 || !p.Enabled || p.Runs != 5 || p.CreatedByUserId != 7 || p.Description != "says hi" {
		t.Fatalf("preprocessor decode: %+v", p)
	}

	var one madtomv1.NoxGetPreprocessorResponse
	decodeInto(t, `{"preprocessor":{"id":1,"name":"g"},"code":"def handle(e,s): pass","static_actions_json":"[]","state_json":"{\"n\":3}"}`, &one)
	if one.Preprocessor.GetName() != "g" || one.Code == "" || one.StateJson != `{"n":3}` {
		t.Fatalf("get decode: %+v", &one)
	}

	var lg madtomv1.NoxListPreprocessorLogsResponse
	decodeInto(t, `{"entries":[{"id":10,"chat_id":-100,"preprocessor_id":1,"preprocessor_name":"g","at":"2026-11-20T06:00:01Z",
		"event_kind":"message","message_id":55,"cron_id":0,"outcome":"say","detail":"hi","duration_ms":140}],"total":321}`, &lg)
	if lg.Total != 321 || lg.Entries[0].Outcome != "say" || lg.Entries[0].DurationMs != 140 {
		t.Fatalf("logs decode: %+v", lg.Entries)
	}

	var cr madtomv1.NoxListCronsResponse
	decodeInto(t, `{"crons":[{"id":3,"chat_id":-100,"name":"daily","preprocessor_id":1,"preprocessor_name":"g","spec_kind":"cron",
		"spec":"0 9 * * *","timezone":"Asia/Tokyo","next_fire_at":"2026-11-21T00:00:00Z","last_fire_at":"","fires":4,
		"max_fires":0,"enabled":true,"depth":1,"origin_cron_id":2,"created_by_user_id":7,"created_at":"x","payload_json":"{}","expires_at":""}]}`, &cr)
	c := cr.Crons[0]
	if c.Spec != "0 9 * * *" || c.Depth != 1 || c.OriginCronId != 2 || !c.Enabled || c.Fires != 4 {
		t.Fatalf("cron decode: %+v", c)
	}

	var ok madtomv1.NoxAutomationActionResponse
	decodeInto(t, `{"ok":false,"error":"unknown action"}`, &ok)
	if ok.Ok || ok.Error != "unknown action" {
		t.Fatalf("action decode: %+v", &ok)
	}
}
