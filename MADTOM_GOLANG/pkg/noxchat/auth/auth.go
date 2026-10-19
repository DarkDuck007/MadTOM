package auth

import (
	"crypto/ed25519"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"

	"golang.org/x/crypto/chacha20poly1305"
	"golang.org/x/crypto/hkdf"
)

const (
	KeyPrefixV1   = "noxchat-ed25519-v1:"
	PubKeyPrefix  = "noxchat-ed25519-pub:"
	PrivKeyPrefix = "noxchat-ed25519-priv:"
)

// KeyPair holds verification keys (Ed25519 public key and/or HMAC key).
type KeyPair struct {
	Ed25519PublicKey ed25519.PublicKey
	RawSecret        []byte
	HmacKey          []byte
	AeadKey          []byte
}

// GenerateEd25519KeyPair creates a new public/private pair.
func GenerateEd25519KeyPair() (pubKeyStr string, privKeyStr string, err error) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return "", "", err
	}
	pubKeyStr = PubKeyPrefix + base64.RawURLEncoding.EncodeToString(pub)
	privKeyStr = PrivKeyPrefix + base64.RawURLEncoding.EncodeToString(priv)
	return pubKeyStr, privKeyStr, nil
}

// GenerateClientKey creates a new client secret formatted as `noxchat-ed25519-v1:<base64url>`.
func GenerateClientKey() (string, error) {
	b := make([]byte, 32)
	if _, err := io.ReadFull(rand.Reader, b); err != nil {
		return "", err
	}
	encoded := base64.RawURLEncoding.EncodeToString(b)
	return KeyPrefixV1 + encoded, nil
}

// ParseKey parses a key string into KeyPair.
// Supports:
// - "noxchat-ed25519-pub:<base64>" (Server verification with public key)
// - "noxchat-ed25519-v1:<base64>" (Symmetric/legacy token or seed)
// - OpenSSH "ssh-ed25519 <base64>"
func ParseKey(keyStr string) (*KeyPair, error) {
	trimmed := strings.Trim(strings.TrimSpace(keyStr), "\"' \r\n\t")
	if trimmed == "" {
		return nil, fmt.Errorf("empty key")
	}

	// 1. Asymmetric public key
	if strings.HasPrefix(trimmed, PubKeyPrefix) {
		rawB64 := strings.TrimPrefix(trimmed, PubKeyPrefix)
		pubBytes, err := decodeBase64Flexible(rawB64)
		if err != nil {
			return nil, fmt.Errorf("failed to decode ed25519 public key: %w", err)
		}
		if len(pubBytes) != ed25519.PublicKeySize {
			return nil, fmt.Errorf("invalid ed25519 public key length: expected 32, got %d", len(pubBytes))
		}
		return &KeyPair{
			Ed25519PublicKey: ed25519.PublicKey(pubBytes),
		}, nil
	}

	// 2. OpenSSH ed25519 format e.g. "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI..."
	if strings.HasPrefix(trimmed, "ssh-ed25519 ") {
		parts := strings.Fields(trimmed)
		if len(parts) >= 2 {
			sshBytes, err := decodeBase64Flexible(parts[1])
			if err == nil && len(sshBytes) >= 32 {
				// Last 32 bytes of ssh wire format is the public key
				pubBytes := sshBytes[len(sshBytes)-32:]
				return &KeyPair{
					Ed25519PublicKey: ed25519.PublicKey(pubBytes),
				}, nil
			}
		}
	}

	// 3. Symmetric v1 or seed format: "noxchat-ed25519-v1:<base64>"
	rawB64 := trimmed
	if strings.HasPrefix(trimmed, KeyPrefixV1) {
		rawB64 = strings.TrimPrefix(trimmed, KeyPrefixV1)
	}

	secret, err := decodeBase64Flexible(rawB64)
	if err != nil {
		return nil, fmt.Errorf("failed to decode secret key: %w", err)
	}

	if len(secret) < 16 {
		return nil, fmt.Errorf("secret too short: must be at least 16 bytes")
	}

	// Derive HMAC key and AEAD key using HKDF-SHA256
	hmacKey := make([]byte, 32)
	hmacReader := hkdf.New(sha256.New, secret, []byte("noxchat-salt-v1"), []byte("noxchat-hmac-v1"))
	if _, err := io.ReadFull(hmacReader, hmacKey); err != nil {
		return nil, fmt.Errorf("hkdf hmac derivation failed: %w", err)
	}

	aeadKey := make([]byte, chacha20poly1305.KeySize)
	aeadReader := hkdf.New(sha256.New, secret, []byte("noxchat-salt-v1"), []byte("noxchat-aead-v1"))
	if _, err := io.ReadFull(aeadReader, aeadKey); err != nil {
		return nil, fmt.Errorf("hkdf aead derivation failed: %w", err)
	}

	kp := &KeyPair{
		RawSecret: secret,
		HmacKey:   hmacKey,
		AeadKey:   aeadKey,
	}

	// If secret is 32 bytes (seed), derive the Ed25519 public key so signatures can be verified
	if len(secret) == ed25519.SeedSize {
		privKey := ed25519.NewKeyFromSeed(secret)
		kp.Ed25519PublicKey = privKey.Public().(ed25519.PublicKey)
	}

	return kp, nil
}

func decodeBase64Flexible(s string) ([]byte, error) {
	b, err := base64.RawURLEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	b, err = base64.StdEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	b, err = base64.URLEncoding.DecodeString(s)
	if err == nil {
		return b, nil
	}
	return base64.RawStdEncoding.DecodeString(s)
}

// LoadKey loads the key string from command line, environment variable, or file path.
func LoadKey(cliKey, cliFile, envName, defaultEnvPath string) (*KeyPair, error) {
	if cliKey != "" {
		return ParseKey(cliKey)
	}

	if envVal := os.Getenv(envName); envVal != "" {
		return ParseKey(envVal)
	}

	if cliFile != "" {
		content, err := os.ReadFile(cliFile)
		if err != nil {
			return nil, fmt.Errorf("failed to read key file '%s': %w", cliFile, err)
		}
		str := string(content)
		for _, line := range strings.Split(str, "\n") {
			line = strings.TrimSpace(line)
			if strings.HasPrefix(line, envName+"=") {
				val := strings.Trim(strings.TrimPrefix(line, envName+"="), "\"' \r\n\t")
				return ParseKey(val)
			}
		}
		return ParseKey(strings.TrimSpace(str))
	}

	if defaultEnvPath != "" {
		if content, err := os.ReadFile(defaultEnvPath); err == nil {
			for _, line := range strings.Split(string(content), "\n") {
				line = strings.TrimSpace(line)
				if strings.HasPrefix(line, envName+"=") {
					val := strings.Trim(strings.TrimPrefix(line, envName+"="), "\"' \r\n\t")
					return ParseKey(val)
				}
			}
		}
	}

	return nil, fmt.Errorf("no auth key provided (set -auth-key, -auth-key-file, or %s in environment/.env)", envName)
}

// VerifySignature verifies a request signature given timestamp, nonce, and signature.
func (k *KeyPair) VerifySignature(tsStr, nonceStr, signatureStr string) bool {
	ts, err := strconv.ParseInt(tsStr, 10, 64)
	if err != nil {
		return false
	}

	// Signature valid within 5 minutes skew
	now := time.Now().Unix()
	if ts < now-300 || ts > now+300 {
		return false
	}

	sig, err := decodeBase64Flexible(signatureStr)
	if err != nil {
		return false
	}

	signedData := []byte(tsStr + ":" + nonceStr)

	// 1. If Ed25519 Public Key is present, try asymmetric Ed25519 verification
	if len(k.Ed25519PublicKey) == ed25519.PublicKeySize && len(sig) == ed25519.SignatureSize {
		if ed25519.Verify(k.Ed25519PublicKey, signedData, sig) {
			return true
		}
	}

	// 2. Fallback to HMAC verification if HMAC key is configured
	if len(k.HmacKey) > 0 {
		mac := hmac.New(sha256.New, k.HmacKey)
		mac.Write(signedData)
		expected := mac.Sum(nil)
		if subtle.ConstantTimeCompare(sig, expected) == 1 {
			return true
		}
	}

	return false
}

// SealPayload seals data using ChaCha20-Poly1305.
func (k *KeyPair) SealPayload(plaintext []byte) ([]byte, error) {
	if len(k.AeadKey) == 0 {
		return nil, fmt.Errorf("AEAD key not available")
	}
	aead, err := chacha20poly1305.New(k.AeadKey)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, aead.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return nil, err
	}
	sealed := aead.Seal(nonce, nonce, plaintext, nil)
	return sealed, nil
}

// OpenPayload opens data sealed with ChaCha20-Poly1305.
func (k *KeyPair) OpenPayload(ciphertext []byte) ([]byte, error) {
	if len(k.AeadKey) == 0 {
		return nil, fmt.Errorf("AEAD key not available")
	}
	aead, err := chacha20poly1305.New(k.AeadKey)
	if err != nil {
		return nil, err
	}
	if len(ciphertext) < aead.NonceSize() {
		return nil, fmt.Errorf("ciphertext too short")
	}
	nonce := ciphertext[:aead.NonceSize()]
	actualCiphertext := ciphertext[aead.NonceSize():]
	return aead.Open(nil, nonce, actualCiphertext, nil)
}
