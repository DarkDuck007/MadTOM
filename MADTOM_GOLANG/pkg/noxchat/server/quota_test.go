package server

import (
	"testing"

	madtomv1 "github.com/DarkDuck007/madtom/pkg/proto/v1"
)

func TestQuotaRequestsUsePythonFieldNames(t *testing.T) {
	list := tipKeys(t, &madtomv1.NoxListUserQuotasRequest{Limit: 25})
	if list["limit"] != float64(25) {
		t.Fatalf("list request payload: %v", list)
	}

	get := tipKeys(t, &madtomv1.NoxGetUserQuotaRequest{UserId: 42, ChatId: -100123})
	if get["user_id"] != float64(42) || get["chat_id"] != float64(-100123) {
		t.Fatalf("get request payload: %v", get)
	}

	set := tipKeys(t, &madtomv1.NoxSetUserQuotaRequest{
		UserId:   42,
		Scope:    "chat",
		ChatId:   -100123,
		Quota_5H:  10,
		Quota_24H: 40,
		Reset_:    true,
	})
	if set["user_id"] != float64(42) || set["scope"] != "chat" || set["chat_id"] != float64(-100123) ||
		set["quota_5h"] != float64(10) || set["quota_24h"] != float64(40) || set["reset"] != true {
		t.Fatalf("set request payload: %v", set)
	}
}

func TestQuotaRepliesDecodeFromPythonShapesIncludingNulls(t *testing.T) {
	var list madtomv1.NoxListUserQuotasResponse
	decodeInto(t, `{
		"enabled": true,
		"defaults": {
			"user_limit_5h": 20,
			"user_limit_24h": 60,
			"user_global_limit_5h": 0,
			"user_global_limit_24h": 150
		},
		"overrides": [
			{
				"user_id": 1001,
				"scope": "all_chats",
				"chat_id": 0,
				"quota_5h": 5,
				"quota_24h": 20,
				"updated_at": null,
				"updated_by": null,
				"user_name": "Alice",
				"chat_title": ""
			},
			{
				"user_id": 1002,
				"scope": "chat",
				"chat_id": -100999,
				"quota_5h": 0,
				"quota_24h": 0,
				"updated_at": "2026-11-25T10:00:00Z",
				"updated_by": 99,
				"user_name": "Bob",
				"chat_title": "Dev Room"
			}
		],
		"top_users": [
			{
				"user_id": 1001,
				"user_name": "Alice",
				"calls_5h": 12,
				"calls_24h": 34,
				"chats": 3,
				"exempt": false
			},
			{
				"user_id": 7,
				"user_name": "Admin",
				"calls_5h": 50,
				"calls_24h": 200,
				"chats": 5,
				"exempt": true
			}
		]
	}`, &list)

	if !list.Enabled || list.Defaults == nil || list.Defaults.UserLimit_5H != 20 || list.Defaults.UserLimit_24H != 60 ||
		list.Defaults.UserGlobalLimit_5H != 0 || list.Defaults.UserGlobalLimit_24H != 150 {
		t.Fatalf("list defaults decode: %+v", &list)
	}
	if len(list.Overrides) != 2 || list.Overrides[0].UpdatedAt != "" || list.Overrides[0].UpdatedBy != 0 ||
		list.Overrides[0].UserName != "Alice" || list.Overrides[1].ChatTitle != "Dev Room" || list.Overrides[1].UpdatedBy != 99 {
		t.Fatalf("list overrides decode: %+v", list.Overrides)
	}
	if len(list.TopUsers) != 2 || list.TopUsers[0].Calls_24H != 34 || !list.TopUsers[1].Exempt {
		t.Fatalf("list top_users decode: %+v", list.TopUsers)
	}

	var get madtomv1.NoxGetUserQuotaResponse
	decodeInto(t, `{
		"user_id": 1001,
		"user_name": "Alice",
		"chat_id": 0,
		"exempt": false,
		"user": {
			"source": "default",
			"count_5h": 0,
			"limit_5h": 0,
			"remaining_5h": null,
			"pct_5h": null,
			"reset_5h": null,
			"count_24h": 0,
			"limit_24h": 0,
			"remaining_24h": null,
			"pct_24h": null,
			"reset_24h": null
		},
		"global": {
			"source": "override",
			"count_5h": 10,
			"limit_5h": 10,
			"remaining_5h": 0,
			"pct_5h": 100.0,
			"reset_5h": "2026-11-25T15:00:00Z",
			"count_24h": 34,
			"limit_24h": 150,
			"remaining_24h": 116,
			"pct_24h": 22.67,
			"reset_24h": "2026-11-26T00:00:00Z"
		},
		"overrides": [
			{
				"user_id": 1001,
				"scope": "global",
				"chat_id": 0,
				"quota_5h": 10,
				"quota_24h": 150,
				"updated_at": null,
				"updated_by": null
			}
		]
	}`, &get)

	if get.UserId != 1001 || get.UserName != "Alice" || get.User == nil || get.Global == nil {
		t.Fatalf("get top-level decode: %+v", &get)
	}
	if get.User.Remaining_5H != nil || get.User.Pct_5H != nil || get.User.Reset_5H != "" {
		t.Fatalf("expected null user tier fields to decode as nil/empty: %+v", get.User)
	}
	if get.Global.Source != "override" || get.Global.Remaining_5H == nil || *get.Global.Remaining_5H != 0 ||
		get.Global.Pct_5H == nil || *get.Global.Pct_5H != 100.0 || get.Global.Reset_5H != "2026-11-25T15:00:00Z" ||
		get.Global.Remaining_24H == nil || *get.Global.Remaining_24H != 116 {
		t.Fatalf("global tier decode: %+v", get.Global)
	}
	if len(get.Overrides) != 1 || get.Overrides[0].Scope != "global" {
		t.Fatalf("get overrides decode: %+v", get.Overrides)
	}

	var setResp madtomv1.NoxSetUserQuotaResponse
	decodeInto(t, `{"ok": true, "removed": true, "error": ""}`, &setResp)
	if !setResp.Ok || !setResp.Removed || setResp.Error != "" {
		t.Fatalf("set response decode: %+v", &setResp)
	}
}
