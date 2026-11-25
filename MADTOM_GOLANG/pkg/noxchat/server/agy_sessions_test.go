package server

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

// The Python agy_sessions RPC reply (SessionDeltaTracker.snapshot + chat titles) decodes field by field.
func TestAgySessionsReplyDecodesFromPythonShape(t *testing.T) {
	var resp madtomv1.NoxAgySessionsResponse
	decodeInto(t, `{
		"since_unix": 1764089000,
		"history_mode": "agy",
		"settings": {"rebase_growth_tokens": "auto", "rebase_growth_min_tokens": 10000,
			"rebase_growth_max_tokens": 24000, "cold_rebuild_after_seconds": 900, "max_session_turns": 0,
			"max_context_tokens": 65536, "working_dialogue_turns": 70},
		"rebuilds": {"new": 3, "growth": 2, "cold": 4, "max_turns": 0, "expired": 1, "reset": 5},
		"input_tokens": {"bootstrap": {"uncached": 54000, "cached": 0, "calls": 3},
			"warm": {"uncached": 8100, "cached": 51000, "calls": 3}},
		"warm_cache_hit": 0.862,
		"sessions": [{"chat_id": -1004384642381, "chat_title": "Group", "epoch": 2, "turns": 5,
			"tokens": 23500, "bootstrap_tokens": 18000, "rebuild_at_tokens": 31000,
			"growth_budget_tokens": 13000, "growth_mode": "auto", "growth_per_turn": 1043.5,
			"cache_hit": 0.88, "idle_seconds": 1200, "age_seconds": 3600,
			"started_reason": "cold", "next_turn_rebuilds": "cold"}]
	}`, &resp)
	if resp.HistoryMode != "agy" || resp.SinceUnix != 1764089000 || resp.WarmCacheHit != 0.862 {
		t.Fatalf("top level: %+v", &resp)
	}
	st := resp.Settings
	if st.RebaseGrowthTokens != "auto" || st.ColdRebuildAfterSeconds != 900 || st.WorkingDialogueTurns != 70 ||
		st.RebaseGrowthMaxTokens != 24000 {
		t.Fatalf("settings: %+v", st)
	}
	r := resp.Rebuilds
	if r.New != 3 || r.Growth != 2 || r.Cold != 4 || r.Expired != 1 || r.Reset_ != 5 {
		t.Fatalf("rebuilds: %+v", r)
	}
	if resp.InputTokens.Warm.Cached != 51000 || resp.InputTokens.Bootstrap.Uncached != 54000 {
		t.Fatalf("input tokens: %+v", resp.InputTokens)
	}
	if len(resp.Sessions) != 1 {
		t.Fatalf("sessions: %d", len(resp.Sessions))
	}
	s := resp.Sessions[0]
	if s.ChatId != -1004384642381 || s.ChatTitle != "Group" || s.Tokens != 23500 || s.GrowthPerTurn != 1043.5 ||
		s.CacheHit != 0.88 || s.StartedReason != "cold" || s.NextTurnRebuilds != "cold" || s.RebuildAtTokens != 31000 {
		t.Fatalf("session: %+v", s)
	}
}
