package auth

import (
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

func newSigner(t *testing.T) (pubStr string, sign func(ts, nonce string) string) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	pubStr = PubKeyPrefix + base64.RawURLEncoding.EncodeToString(pub)
	return pubStr, func(ts, nonce string) string {
		return base64.RawURLEncoding.EncodeToString(ed25519.Sign(priv, []byte(ts+":"+nonce)))
	}
}

func nowTs() string { return strconv.FormatInt(time.Now().Unix(), 10) }

// ---------------------------------------------------------------- replay cache

func TestReplayCacheRejectsSecondUseWithinWindow(t *testing.T) {
	rc := NewReplayCache(10*time.Minute, 1000)
	now := time.Now()
	if rc.Seen(now.Unix(), "nonce-aaaaaaaa", now) {
		t.Fatal("first use must not be a replay")
	}
	if !rc.Seen(now.Unix(), "nonce-aaaaaaaa", now.Add(time.Second)) {
		t.Fatal("second use must be a replay")
	}
	if rc.Seen(now.Unix(), "nonce-bbbbbbbb", now) {
		t.Fatal("different nonce must not be a replay")
	}
}

func TestReplayCacheForgetsAfterWindowAndIsBounded(t *testing.T) {
	rc := NewReplayCache(time.Minute, 3)
	now := time.Now()
	rc.Seen(now.Unix(), "n-00000001", now)
	if rc.Seen(now.Unix(), "n-00000001", now.Add(2*time.Minute)) {
		t.Fatal("entry older than the window must be forgotten")
	}
	for i := 0; i < 50; i++ {
		rc.Seen(now.Unix(), fmt.Sprintf("n-%08d", i+10), now)
	}
	if rc.Len() > 3 {
		t.Fatalf("cache must stay bounded, got %d", rc.Len())
	}
}

func TestReplayCacheConcurrent(t *testing.T) {
	rc := NewReplayCache(time.Minute, 10000)
	now := time.Now()
	var wg sync.WaitGroup
	fresh := make(chan bool, 64)
	for i := 0; i < 64; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			fresh <- !rc.Seen(now.Unix(), "same-nonce-xyz", now)
		}()
	}
	wg.Wait()
	close(fresh)
	n := 0
	for ok := range fresh {
		if ok {
			n++
		}
	}
	if n != 1 {
		t.Fatalf("exactly one concurrent caller may win, got %d", n)
	}
}

// ---------------------------------------------------------------- verifier

func TestVerifierAcceptsOnceThenRejectsReplay(t *testing.T) {
	pub, sign := newSigner(t)
	kp, err := ParseKey(pub)
	if err != nil {
		t.Fatal(err)
	}
	v := NewVerifier([]*KeyPair{kp}, NewReplayCache(10*time.Minute, 1000))
	ts, nonce := nowTs(), "0123456789abcdef"
	if err := v.Verify(ts, nonce, sign(ts, nonce)); err != nil {
		t.Fatalf("valid signature rejected: %v", err)
	}
	if err := v.Verify(ts, nonce, sign(ts, nonce)); err == nil || !strings.Contains(err.Error(), "replay") {
		t.Fatalf("replay must be rejected with a replay error, got %v", err)
	}
}

func TestVerifierRejectsBadInputs(t *testing.T) {
	pub, sign := newSigner(t)
	kp, _ := ParseKey(pub)
	v := NewVerifier([]*KeyPair{kp}, NewReplayCache(10*time.Minute, 1000))
	ts := nowTs()
	old := strconv.FormatInt(time.Now().Add(-10*time.Minute).Unix(), 10)
	_, otherSign := newSigner(t)
	cases := map[string][3]string{
		"stale ts":     {old, "0123456789abcdef", sign(old, "0123456789abcdef")},
		"short nonce":  {ts, "abc", sign(ts, "abc")},
		"long nonce":   {ts, strings.Repeat("n", 200), sign(ts, strings.Repeat("n", 200))},
		"wrong key":    {ts, "fedcba9876543210", otherSign(ts, "fedcba9876543210")},
		"empty sig":    {ts, "fedcba9876543211", ""},
		"tampered ts":  {nowTs() + "0", "fedcba9876543212", sign(ts, "fedcba9876543212")},
	}
	for name, c := range cases {
		if err := v.Verify(c[0], c[1], c[2]); err == nil {
			t.Errorf("%s: expected rejection", name)
		}
	}
	// a rejected attempt must not burn the nonce for the legitimate client
	if err := v.Verify(ts, "fedcba9876543210", sign(ts, "fedcba9876543210")); err != nil {
		t.Fatalf("nonce used in a failed attempt must still be usable: %v", err)
	}
}

func TestVerifierAnyAuthorizedKey(t *testing.T) {
	pubA, signA := newSigner(t)
	pubB, signB := newSigner(t)
	ka, _ := ParseKey(pubA)
	kb, _ := ParseKey(pubB)
	v := NewVerifier([]*KeyPair{ka, kb}, NewReplayCache(time.Minute, 100))
	ts := nowTs()
	if err := v.Verify(ts, "nonce-for-a-0001", signA(ts, "nonce-for-a-0001")); err != nil {
		t.Fatal(err)
	}
	if err := v.Verify(ts, "nonce-for-b-0001", signB(ts, "nonce-for-b-0001")); err != nil {
		t.Fatal(err)
	}
}

func TestVerifierWithoutKeysRejectsEverything(t *testing.T) {
	_, sign := newSigner(t)
	v := NewVerifier(nil, NewReplayCache(time.Minute, 10))
	ts := nowTs()
	if err := v.Verify(ts, "0123456789abcdef", sign(ts, "0123456789abcdef")); err == nil {
		t.Fatal("a verifier with no keys must reject (token-less mode is decided by StartupPolicy, not here)")
	}
}

// ---------------------------------------------------------------- authorized keys

func TestLoadAuthorizedKeys(t *testing.T) {
	pubA, _ := newSigner(t)
	pubB, _ := newSigner(t)
	dir := t.TempDir()
	p := filepath.Join(dir, "authorized_keys")
	content := "# Studio on the desktop\n" + pubA + "\n\n   " + pubB + "   # laptop\n"
	if err := os.WriteFile(p, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	keys, err := LoadAuthorizedKeys(p)
	if err != nil {
		t.Fatal(err)
	}
	if len(keys) != 2 {
		t.Fatalf("want 2 keys, got %d", len(keys))
	}
	bad := filepath.Join(dir, "bad")
	os.WriteFile(bad, []byte(pubA+"\nnot-a-key\n"), 0o600)
	if _, err := LoadAuthorizedKeys(bad); err == nil || !strings.Contains(err.Error(), "line 2") {
		t.Fatalf("want an error naming line 2, got %v", err)
	}
	empty := filepath.Join(dir, "empty")
	os.WriteFile(empty, []byte("# nothing\n"), 0o600)
	if _, err := LoadAuthorizedKeys(empty); err == nil {
		t.Fatal("a file without keys must be an error")
	}
}

func TestLoadAuthorizedKeysRejectsSecretKeys(t *testing.T) {
	// authorized_keys is for public keys only; a shared secret there would be a misconfiguration
	secret, _ := GenerateClientKey()
	p := filepath.Join(t.TempDir(), "ak")
	os.WriteFile(p, []byte(secret+"\n"), 0o600)
	if _, err := LoadAuthorizedKeys(p); err == nil {
		t.Fatal("secret (non-public) keys must be refused in authorized_keys")
	}
}

// ---------------------------------------------------------------- startup policy

func TestStartupPolicy(t *testing.T) {
	if err := StartupPolicy(0, false); err == nil {
		t.Fatal("no keys and no -insecure-dev must refuse to start")
	}
	if err := StartupPolicy(0, true); err != nil {
		t.Fatalf("-insecure-dev allows token-less mode: %v", err)
	}
	if err := StartupPolicy(1, false); err != nil {
		t.Fatal(err)
	}
}

// ---------------------------------------------------------------- persistent TLS

func TestLoadOrCreateTLSPersistsAndPins(t *testing.T) {
	dir := t.TempDir()
	cfg1, fp1, err := LoadOrCreateTLS(dir, "node-a")
	if err != nil {
		t.Fatal(err)
	}
	if len(cfg1.Certificates) != 1 || len(fp1) != 64 {
		t.Fatalf("want one cert and a hex sha256 fingerprint, got %d / %q", len(cfg1.Certificates), fp1)
	}
	der := cfg1.Certificates[0].Certificate[0]
	sum := sha256.Sum256(der)
	if hex.EncodeToString(sum[:]) != fp1 {
		t.Fatal("fingerprint must be sha256 of the leaf DER")
	}
	keyInfo, err := os.Stat(filepath.Join(dir, "tls", "key.pem"))
	if err != nil {
		t.Fatal(err)
	}
	if keyInfo.Mode().Perm() != 0o600 {
		t.Fatalf("key.pem must be 0600, got %v", keyInfo.Mode().Perm())
	}
	_, fp2, err := LoadOrCreateTLS(dir, "node-a")
	if err != nil {
		t.Fatal(err)
	}
	if fp1 != fp2 {
		t.Fatal("second start must reuse the persisted certificate")
	}
	if cfg1.MinVersion < 0x0304 {
		t.Fatal("TLS 1.3 minimum required")
	}
}

func TestLoadOrCreateTLSRegeneratesExpiredOrCorrupt(t *testing.T) {
	dir := t.TempDir()
	_, fp1, err := LoadOrCreateTLS(dir, "node-a")
	if err != nil {
		t.Fatal(err)
	}
	certPath := filepath.Join(dir, "tls", "cert.pem")
	os.WriteFile(certPath, []byte("garbage"), 0o644)
	_, fp2, err := LoadOrCreateTLS(dir, "node-a")
	if err != nil {
		t.Fatal(err)
	}
	if fp2 == fp1 {
		t.Fatal("corrupt cert must be regenerated")
	}
	raw, _ := os.ReadFile(certPath)
	blk, _ := pem.Decode(raw)
	if blk == nil {
		t.Fatal("regenerated cert must be PEM")
	}
	c, err := x509.ParseCertificate(blk.Bytes)
	if err != nil {
		t.Fatal(err)
	}
	if time.Until(c.NotAfter) < 300*24*time.Hour {
		t.Fatal("certificate should be valid for about a year or more")
	}
}
