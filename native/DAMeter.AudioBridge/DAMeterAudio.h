#ifndef DAMETER_AUDIO_H
#define DAMETER_AUDIO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Callbacks are serialized on a native audio queue. Data/message pointers are
// valid only during the callback; copy any bytes needed after returning.
typedef void (*dameter_audio_pcm_callback)(void *context, const uint8_t *pcm, int32_t byte_count);
typedef void (*dameter_audio_status_callback)(void *context, int32_t code, const char *message);

// 1 = progress, 2 = stream ready, 77 = permission denied, 70 = other failure.
// 0 = STOPPED: the final callback, after which no callback will use context.
// Keep the callback functions and context alive until status 0, including when
// macOS leaves an authorization/stop request pending after a stop request.
void *dameter_audio_start(void *context, dameter_audio_pcm_callback pcm, dameter_audio_status_callback status);

// Idempotent and nonblocking. The handle remains owned by the caller.
void dameter_audio_stop(void *handle);

// Call exactly once, after status 0; calling from the final callback is valid.
void dameter_audio_release(void *handle);

// Returns required UTF-8 bytes including NUL for Bundle.main.bundleIdentifier.
// Pass NULL/0 to query size. A short buffer receives a NUL-terminated prefix.
int32_t dameter_audio_identity(uint8_t *buffer, int32_t capacity);

// Returns 0 on success, 70 on failure. Does not request capture permission.
int32_t dameter_audio_self_test(void);

#ifdef __cplusplus
}
#endif

#endif
