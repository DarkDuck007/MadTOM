package auth

import (
	"bufio"
	"context"
	"crypto/ed25519"
	"encoding/base64"
	"errors"
	"fmt"
	"os"
	"strings"
	"time"
	"unicode/utf8"

	"golang.org/x/crypto/ssh"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
)

type Verifier struct {
	keys   []*KeyPair
	replay *ReplayCache
}

func NewVerifier(keys []*KeyPair, rc *ReplayCache) *Verifier {
	if rc == nil {
		rc = NewReplayCache(10*time.Minute, 65536)
	}
	return &Verifier{keys: append([]*KeyPair(nil), keys...), replay: rc}
}

func (v *Verifier) Verify(tsStr, nonce, signature string) error {
	if v == nil || len(v.keys) == 0 {
		return errors.New("no authorization keys configured")
	}
	now := time.Now()
	ts, err := verifyTimestamp(tsStr, now)
	if err != nil {
		return err
	}
	if len(nonce) < 8 || len(nonce) > 128*utf8.UTFMax || !utf8.ValidString(nonce) {
		return errors.New("invalid authentication nonce")
	}
	if chars := utf8.RuneCountInString(nonce); chars < 8 || chars > 128 {
		return errors.New("invalid authentication nonce length")
	}
	if len(signature) > base64.StdEncoding.EncodedLen(ed25519.SignatureSize) {
		return errors.New("invalid authentication signature")
	}
	sig, err := decodeBase64Flexible(signature)
	if err != nil {
		return errors.New("invalid authentication signature")
	}
	signedData := []byte(tsStr + ":" + nonce)
	for _, key := range v.keys {
		if key != nil && key.verifySignature(signedData, sig) {
			// Use the same whole-second precision as timestamp validation at the skew boundaries.
			if v.replay.Seen(ts, nonce, now.Truncate(time.Second)) {
				return errors.New("authentication replay")
			}
			return nil
		}
	}
	return errors.New("cryptographic signature verification failed")
}

// VerifyIncoming authenticates signed gRPC metadata. A nil verifier allows dev traffic.
func (v *Verifier) VerifyIncoming(ctx context.Context) error {
	if v == nil {
		return nil
	}
	md, _ := metadata.FromIncomingContext(ctx)
	ts, nonce, sig := md.Get("x-nox-auth-ts"), md.Get("x-nox-auth-nonce"), md.Get("x-nox-auth-sig")
	if len(ts) != 1 || len(nonce) != 1 || len(sig) != 1 {
		return status.Error(codes.Unauthenticated, "missing or ambiguous authentication metadata")
	}
	if err := v.Verify(ts[0], nonce[0], sig[0]); err != nil {
		return status.Error(codes.Unauthenticated, err.Error())
	}
	return nil
}

func (v *Verifier) UnaryInterceptor(ctx context.Context, req any, _ *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (any, error) {
	if err := v.VerifyIncoming(ctx); err != nil {
		return nil, err
	}
	return handler(ctx, req)
}

func (v *Verifier) StreamInterceptor(srv any, stream grpc.ServerStream, _ *grpc.StreamServerInfo, handler grpc.StreamHandler) error {
	if err := v.VerifyIncoming(stream.Context()); err != nil {
		return err
	}
	return handler(srv, stream)
}

// LoadAuthorizedKeys reads an Ed25519 public key per line.
func LoadAuthorizedKeys(path string) ([]*KeyPair, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	var keys []*KeyPair
	scanner := bufio.NewScanner(file)
	lineNum := 0
	for scanner.Scan() {
		lineNum++
		line, _, _ := strings.Cut(scanner.Text(), "#")
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		key, err := parseAuthorizedKey(line)
		if err != nil {
			return nil, fmt.Errorf("line %d: %w", lineNum, err)
		}
		keys = append(keys, key)
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("line %d: %w", lineNum+1, err)
	}
	if len(keys) == 0 {
		return nil, errors.New("authorized keys file has no public keys")
	}
	return keys, nil
}

func parseAuthorizedKey(line string) (*KeyPair, error) {
	if strings.HasPrefix(line, PubKeyPrefix) {
		return ParseKey(line)
	}
	fields := strings.Fields(line)
	if fields[0] != "ssh-ed25519" || len(fields) < 2 {
		return nil, errors.New("expected an Ed25519 public key")
	}
	wire, err := base64.StdEncoding.DecodeString(fields[1])
	if err != nil {
		return nil, errors.New("invalid OpenSSH Ed25519 public key encoding")
	}
	key, err := ssh.ParsePublicKey(wire)
	if err != nil || key.Type() != ssh.KeyAlgoED25519 {
		return nil, errors.New("invalid OpenSSH Ed25519 public key")
	}
	return &KeyPair{Ed25519PublicKey: key.(ssh.CryptoPublicKey).CryptoPublicKey().(ed25519.PublicKey)}, nil
}

func StartupPolicy(nKeys int, insecureDev bool) error {
	if nKeys <= 0 && !insecureDev {
		return errors.New("authentication requires a key or explicit -insecure-dev")
	}
	return nil
}

// LoadProbeVerifier combines the legacy key source with a public-key allowlist.
func LoadProbeVerifier(cliKey, cliFile, envName, defaultEnvPath, authorizedKeys string, insecureDev bool) (*Verifier, error) {
	var keys []*KeyPair
	if authorizedKeys != "" {
		var err error
		keys, err = LoadAuthorizedKeys(authorizedKeys)
		if err != nil {
			return nil, fmt.Errorf("authorized keys %q: %w", authorizedKeys, err)
		}
	}
	key, err := LoadKey(cliKey, cliFile, envName, defaultEnvPath)
	switch {
	case err == nil:
		keys = append(keys, key)
	case !errors.Is(err, errNoKey):
		return nil, err
	}
	if err := StartupPolicy(len(keys), insecureDev); err != nil {
		return nil, err
	}
	if len(keys) == 0 {
		return nil, nil
	}
	return NewVerifier(keys, nil), nil
}
