package server

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestBanRequestsUsePythonFieldNames(t *testing.T) {
	list := tipKeys(t, &madtomv1.NoxBanListRequest{IncludeInactive: true})
	if list["include_inactive"] != true {
		t.Fatalf("list request payload: %v", list)
	}
	act := tipKeys(t, &madtomv1.NoxBanActionRequest{Action: "lift", UserId: 4242, Reason: "apologised"})
	if act["action"] != "lift" || act["user_id"] != float64(4242) || act["reason"] != "apologised" {
		t.Fatalf("action request payload: %v", act)
	}
}

func TestBanListDecodesFromPythonShape(t *testing.T) {
	var resp madtomv1.NoxBanListResponse
	decodeInto(t, `{"bans": [{
		"user_id": 4242, "user_name": "Bully", "strikes": 2, "banned_until": "2026-12-12T12:00:00.000000Z",
		"mode": "react", "emoji": "🖕", "reason": "constant insults", "evidence": [1, 2],
		"source_chat_id": -1003920939724, "created_at": "2026-11-28T12:00:00Z", "updated_at": "2026-11-28T12:00:00Z",
		"lifted_at": "", "lifted_by": "", "active": true, "seconds_left": 1209600,
		"history": [{"chat_id": -1003920939724, "action": "ban", "days": 14, "reason": "constant insults",
		             "actor": "Nox", "created_at": "2026-11-28T12:00:00Z"},
		            {"chat_id": 0, "action": "lift", "days": 0, "reason": "", "actor": "admin", "created_at": "x"}]
	}]}`, &resp)
	if len(resp.Bans) != 1 {
		t.Fatalf("bans: %v", resp.Bans)
	}
	b := resp.Bans[0]
	if b.UserId != 4242 || b.Strikes != 2 || b.Mode != "react" || b.Emoji != "🖕" || !b.Active ||
		b.SecondsLeft != 1209600 || b.SourceChatId != -1003920939724 || len(b.Evidence) != 2 || b.Evidence[1] != 2 {
		t.Fatalf("ban decoded wrong: %+v", b)
	}
	if len(b.History) != 2 || b.History[0].Days != 14 || b.History[0].Action != "ban" || b.History[1].Actor != "admin" {
		t.Fatalf("history decoded wrong: %+v", b.History)
	}

	var act madtomv1.NoxBanActionResponse
	decodeInto(t, `{"ok": true, "message": "Ban lifted"}`, &act)
	if !act.Ok || act.Message != "Ban lifted" {
		t.Fatalf("action decoded wrong: %+v", &act)
	}
}
