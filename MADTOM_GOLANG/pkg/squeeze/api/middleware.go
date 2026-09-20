package api

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"log/slog"
	"net/http"
	"strings"
	"time"
)

func (s *Server) middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		var id [12]byte
		_, _ = rand.Read(id[:])
		requestID := hex.EncodeToString(id[:])
		w.Header().Set("X-Request-ID", requestID)
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, HEAD, POST, PATCH, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Authorization, Content-Type, Tus-Resumable, Upload-Offset, Upload-Length, Last-Event-ID, X-HTTP-Method-Override")
		w.Header().Set("Access-Control-Expose-Headers", "Location, Upload-Offset, Upload-Length, Tus-Resumable, Tus-Version, Tus-Max-Size, Content-Disposition, Content-Range, Accept-Ranges, X-Request-ID, Retry-After")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		defer func() {
			if e := recover(); e != nil {
				slog.Error("request panic", "request_id", requestID, "panic", e)
				problem(w, 500, "internal server error")
			}
			slog.Info("request", "request_id", requestID, "method", r.Method, "path", r.URL.Path, "duration", time.Since(start))
		}()
		if r.Method == "OPTIONS" {
			w.Header().Set("Tus-Version", "1.0.0")
			w.Header().Set("Tus-Max-Size", formatInt(s.Config.MaxUpload))
			w.WriteHeader(204)
			return
		}
		if s.Config.Token != "" && subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+s.Config.Token)) != 1 {
			w.Header().Set("WWW-Authenticate", "Bearer")
			problem(w, 401, "bearer token required")
			return
		}
		if strings.HasSuffix(r.URL.Path, "/upload") {
			if method := r.Header.Get("X-HTTP-Method-Override"); method != "" {
				r.Method = method
			}
		}
		next.ServeHTTP(w, r)
	})
}
