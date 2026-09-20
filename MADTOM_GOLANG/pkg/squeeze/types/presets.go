package types

// Preset defines a server-managed transcoding profile.
type Preset struct {
	Key         string  `json:"key" yaml:"key"`
	Title       string  `json:"title" yaml:"title"`
	Category    string  `json:"category" yaml:"category"`
	Description string  `json:"description" yaml:"description"`
	Tag         string  `json:"tag,omitempty" yaml:"tag,omitempty"`
	Spec        JobSpec `json:"spec" yaml:"spec"`
}
