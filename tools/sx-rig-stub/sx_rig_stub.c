/* STUB libsx_rig.so — NOT the rig. Implements sx_rig.h so OpenQuestCapture's binding can be
 * built and exercised before the Rust libsx_rig.so exists. Every device-touching call fails
 * with SX_RIG_E_DEVICE {"code":"no_rig"}, so a start with rig devices plugged in is refused.
 * Never ship this: build.sh writes it under tools/sx-rig-stub/build/, outside Assets/. */
#include <stdio.h>
#include <string.h>
#include "sx_rig.h"

static char g_last_error[512] = "{\"code\":\"none\",\"detail\":\"\"}";
static int g_open = 0;
/* Set by a config containing "stub_long_status": status then exceeds the binding's first
 * buffer, which exercises its SX_RIG_E_BUFFER retry. */
static int g_long_status = 0;
static char g_long[6000];

static int32_t fail(int32_t code, const char *name, const char *detail) {
    snprintf(g_last_error, sizeof g_last_error, "{\"code\":\"%s\",\"detail\":\"%s\"}", name, detail);
    return code;
}

static int32_t emit(const char *json, char *out, size_t cap, size_t *needed) {
    size_t n = strlen(json) + 1;
    if (needed) *needed = n;
    if (out == NULL || cap < n) return SX_RIG_E_BUFFER;
    memcpy(out, json, n);
    return SX_RIG_OK;
}

int32_t sx_rig_open(const char *config_json) {
    if (config_json == NULL || config_json[0] != '{') return fail(SX_RIG_E_ARGUMENT, "argument", "config is not a JSON object");
    g_open = 1;
    g_long_status = strstr(config_json, "\"stub_long_status\"") != NULL;
    return SX_RIG_OK;
}

int32_t sx_rig_enroll(const char *join_code, char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    if (join_code == NULL || strncmp(join_code, "sxj_", 4) != 0) return fail(SX_RIG_E_ARGUMENT, "argument", "not a join code");
    return fail(SX_RIG_E_STATE, "state", "stub libsx_rig: no factory_url to enroll with");
}

int32_t sx_rig_enroll_site(const char *request_json, char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    if (request_json == NULL || request_json[0] != '{') return fail(SX_RIG_E_ARGUMENT, "argument", "request is not a JSON object");
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    return fail(SX_RIG_E_DEVICE, "device", "stub libsx_rig: no Factory");
}

int32_t sx_rig_api_token(char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    return fail(SX_RIG_E_STATE, "state", "stub libsx_rig: not enrolled");
}

int32_t sx_rig_sign_in(char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    return fail(SX_RIG_E_STATE, "state", "stub libsx_rig: not enrolled");
}

int32_t sx_rig_sign_out(void) {
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    return SX_RIG_OK;
}

int32_t sx_rig_command(const char *command_json, char *out_json, size_t cap, size_t *needed) {
    if (command_json == NULL || command_json[0] != '{') return fail(SX_RIG_E_ARGUMENT, "argument", "command is not a JSON object");
    if (strstr(command_json, "\"finalize\"") != NULL) {
        return emit("{\"answer\":{\"stub\":true},\"floor\":null}", out_json, cap, needed);
    }
    return fail(SX_RIG_E_STATE, "state", "stub libsx_rig: no open collection");
}

int32_t sx_rig_report_device(const char *device_json) {
    if (device_json == NULL || device_json[0] != '{') return fail(SX_RIG_E_ARGUMENT, "argument", "device report is not a JSON object");
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    return SX_RIG_OK;
}

int32_t sx_rig_attach_usb(int32_t fd, uint16_t vendor_id, uint16_t product_id, const char *device_name) {
    (void)fd; (void)vendor_id; (void)product_id; (void)device_name;
    if (!g_open) return fail(SX_RIG_E_STATE, "state", "rig is not open");
    return fail(SX_RIG_E_DEVICE, "no_rig", "stub libsx_rig: no rig");
}

int32_t sx_rig_detach_usb(const char *device_name) {
    (void)device_name;
    return fail(SX_RIG_E_DEVICE, "no_rig", "stub libsx_rig: no rig");
}

int32_t sx_rig_check(char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    return fail(SX_RIG_E_DEVICE, "no_rig", "stub libsx_rig: no rig");
}

int32_t sx_rig_start(const char *episode_json, int64_t monotonic_anchor_ns, int64_t utc_anchor_ns) {
    (void)episode_json; (void)monotonic_anchor_ns; (void)utc_anchor_ns;
    return fail(SX_RIG_E_DEVICE, "no_rig", "stub libsx_rig: no rig");
}

int32_t sx_rig_start_armed(const char *episode_json, char *out_json, size_t cap, size_t *needed) {
    (void)episode_json; (void)out_json; (void)cap; (void)needed;
    return fail(SX_RIG_E_DEVICE, "no_rig", "stub libsx_rig: no rig");
}

int32_t sx_rig_stop(char *out_json, size_t cap, size_t *needed) {
    (void)out_json; (void)cap; (void)needed;
    return fail(SX_RIG_E_STATE, "state", "stub libsx_rig: nothing was started");
}

int32_t sx_rig_status(char *out_json, size_t cap, size_t *needed) {
    if (g_open && g_long_status) {
        int n = snprintf(g_long, sizeof g_long, "{\"stub\":true,\"state\":\"no_rig\",\"pad\":\"");
        memset(g_long + n, 'x', sizeof g_long - n - 3);
        memcpy(g_long + sizeof g_long - 3, "\"}", 3);
        return emit(g_long, out_json, cap, needed);
    }
    return emit(g_open
        ? "{\"stub\":true,\"state\":\"no_rig\",\"detail\":\"stub libsx_rig: this build has no rig implementation\"}"
        : "{\"stub\":true,\"state\":\"closed\"}", out_json, cap, needed);
}

int32_t sx_rig_last_error(char *out_json, size_t cap, size_t *needed) {
    return emit(g_last_error, out_json, cap, needed);
}

void sx_rig_close(void) { g_open = 0; }
