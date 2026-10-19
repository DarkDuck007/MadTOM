package auth_test

import (
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/DarkDuck007/madtom/pkg/noxchat/auth"
)

func TestAuthKeyGenerationAndVerification(t *testing.T) {
	keyStr, err := auth.GenerateClientKey()
	if err != nil {
		t.Fatalf("failed to generate key: %v", err)
	}

	pair, err := auth.ParseKey(keyStr)
	if err != nil {
		t.Fatalf("failed to parse generated key: %v", err)
	}

	ts := time.Now().Unix()
	tsStr := string(int64ToString(ts))
	nonceStr := "test-nonce-12345"

	mac := hmac.New(sha256.New, pair.HmacKey)
	mac.Write([]byte(tsStr))
	mac.Write([]byte(":"))
	mac.Write([]byte(nonceStr))
	sigBytes := mac.Sum(nil)
	sigStr := base64.RawURLEncoding.EncodeToString(sigBytes)

	if !pair.VerifySignature(tsStr, nonceStr, sigStr) {
		t.Errorf("expected signature verification to succeed")
	}

	// Tampered nonce
	if pair.VerifySignature(tsStr, "wrong-nonce", sigStr) {
		t.Errorf("expected tampered nonce to fail")
	}

	// Expired timestamp (10 minutes ago)
	expiredTsStr := string(int64ToString(ts - 600))
	if pair.VerifySignature(expiredTsStr, nonceStr, sigStr) {
		t.Errorf("expected expired timestamp to fail")
	}
}

func TestAeadEncryptionDecryption(t *testing.T) {
	keyStr, err := auth.GenerateClientKey()
	if err != nil {
		t.Fatalf("failed to generate key: %v", err)
	}

	pair, err := auth.ParseKey(keyStr)
	if err != nil {
		t.Fatalf("failed to parse key: %v", err)
	}

	plaintext := []byte("hello encrypted noxchat payload 1234567890")
	sealed, err := pair.SealPayload(plaintext)
	if err != nil {
		t.Fatalf("SealPayload failed: %v", err)
	}

	decrypted, err := pair.OpenPayload(sealed)
	if err != nil {
		t.Fatalf("OpenPayload failed: %v", err)
	}

	if string(decrypted) != string(plaintext) {
		t.Errorf("decrypted payload mismatch: got %s, want %s", string(decrypted), string(plaintext))
	}
}

func TestEd25519AsymmetricVerification(t *testing.T) {
	pubKeyStr, privKeyStr, err := auth.GenerateEd25519KeyPair()
	if err != nil {
		t.Fatalf("GenerateEd25519KeyPair failed: %v", err)
	}

	serverPair, err := auth.ParseKey(pubKeyStr)
	if err != nil {
		t.Fatalf("ParseKey failed on public key: %v", err)
	}

	// Client signs using private key
	privRaw := strings.TrimPrefix(privKeyStr, auth.PrivKeyPrefix)
	privBytes, err := base64.RawURLEncoding.DecodeString(privRaw)
	if err != nil {
		t.Fatalf("failed to decode private key: %v", err)
	}

	ts := time.Now().Unix()
	tsStr := string(int64ToString(ts))
	nonceStr := "nonce-ed25519-abc"

	signedData := []byte(tsStr + ":" + nonceStr)
	sig := ed25519.Sign(ed25519.PrivateKey(privBytes), signedData)
	sigStr := base64.RawURLEncoding.EncodeToString(sig)

	// Server verifies using ONLY the public key
	if !serverPair.VerifySignature(tsStr, nonceStr, sigStr) {
		t.Errorf("expected server to successfully verify Ed25519 signature using public key")
	}

	// Tampered data must fail
	if serverPair.VerifySignature(tsStr, "wrong-nonce", sigStr) {
		t.Errorf("expected tampered signature verification to fail")
	}
}

func TestV1SeedKeyDerivationAndVerification(t *testing.T) {
	// A 32-byte seed in v1 format
	seed := make([]byte, 32)
	for i := range seed {
		seed[i] = byte(i + 1)
	}
	seedStr := "noxchat-ed25519-v1:" + base64.RawURLEncoding.EncodeToString(seed)

	serverPair, err := auth.ParseKey(seedStr)
	if err != nil {
		t.Fatalf("ParseKey failed on v1 seed key: %v", err)
	}

	privKey := ed25519.NewKeyFromSeed(seed)
	ts := time.Now().Unix()
	tsStr := string(int64ToString(ts))
	nonceStr := "test-nonce-seed-v1"

	signedData := []byte(tsStr + ":" + nonceStr)
	sig := ed25519.Sign(privKey, signedData)
	sigStr := base64.RawURLEncoding.EncodeToString(sig)

	if !serverPair.VerifySignature(tsStr, nonceStr, sigStr) {
		t.Errorf("expected server to successfully verify Ed25519 signature from v1 seed key")
	}
}

func int64ToString(n int64) []byte {
	return []byte(strconv.FormatInt(n, 10))
}

