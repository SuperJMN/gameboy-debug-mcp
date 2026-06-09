#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "gb.h"

#define GBMCP_WIDTH 160
#define GBMCP_HEIGHT 144
#define GBMCP_PIXELS (GBMCP_WIDTH * GBMCP_HEIGHT)

#if defined(_WIN32)
#define GBMCP_EXPORT __declspec(dllexport)
#else
#define GBMCP_EXPORT __attribute__((visibility("default")))
#endif

typedef struct {
    uint16_t af;
    uint16_t bc;
    uint16_t de;
    uint16_t hl;
    uint16_t sp;
    uint16_t pc;
    uint8_t a;
    uint8_t f;
    uint8_t b;
    uint8_t c;
    uint8_t d;
    uint8_t e;
    uint8_t h;
    uint8_t l;
    bool ime;
    bool halted;
} gbmcp_registers_t;

typedef struct {
    bool found;
    uint16_t pc;
    uint8_t value;
    uint64_t count;
} gbmcp_last_write_t;

typedef struct {
    GB_gameboy_t *gb;
    uint32_t pixels[GBMCP_PIXELS];
    gbmcp_last_write_t last_writes[0x10000];
    char last_error[512];
    char *log_buffer;
    size_t log_length;
    size_t log_capacity;
    bool capture_log;
    bool trace_active;
    bool trace_hit;
    uint16_t trace_address;
    uint16_t trace_pc;
    uint8_t trace_value;
    bool loaded;
    GB_model_t active_model;
} gbmcp_session_t;

static void set_error(gbmcp_session_t *session, const char *message)
{
    if (!session) return;
    snprintf(session->last_error, sizeof(session->last_error), "%s", message);
}

static uint32_t rgb_encode(GB_gameboy_t *gb, uint8_t r, uint8_t g, uint8_t b)
{
    (void)gb;
    return ((uint32_t)r << 16) | ((uint32_t)g << 8) | b;
}

static void append_log(gbmcp_session_t *session, const char *text)
{
    if (!session || !session->capture_log || !text) return;

    size_t text_length = strlen(text);
    size_t required = session->log_length + text_length + 1;
    if (required > session->log_capacity) {
        size_t next_capacity = session->log_capacity ? session->log_capacity : 1024;
        while (next_capacity < required) {
            next_capacity *= 2;
        }

        char *next = realloc(session->log_buffer, next_capacity);
        if (!next) {
            set_error(session, "Could not allocate disassembly log buffer.");
            return;
        }

        session->log_buffer = next;
        session->log_capacity = next_capacity;
    }

    memcpy(session->log_buffer + session->log_length, text, text_length);
    session->log_length += text_length;
    session->log_buffer[session->log_length] = 0;
}

static void log_callback(GB_gameboy_t *gb, const char *string, GB_log_attributes_t attributes)
{
    (void)attributes;
    append_log((gbmcp_session_t *)GB_get_user_data(gb), string);
}

static bool write_callback(GB_gameboy_t *gb, uint16_t address, uint8_t data)
{
    gbmcp_session_t *session = (gbmcp_session_t *)GB_get_user_data(gb);
    if (!session) return true;

    gbmcp_last_write_t *last_write = &session->last_writes[address];
    last_write->found = true;
    last_write->pc = gb->pc;
    last_write->value = data;
    last_write->count++;

    if (session->trace_active && address == session->trace_address) {
        session->trace_hit = true;
        session->trace_pc = gb->pc;
        session->trace_value = data;
    }

    return true;
}

static void configure_core(gbmcp_session_t *session)
{
    GB_set_user_data(session->gb, session);
    GB_set_log_callback(session->gb, log_callback);
    GB_set_write_memory_callback(session->gb, write_callback);
    GB_set_rgb_encode_callback(session->gb, rgb_encode);
    GB_set_pixels_output(session->gb, session->pixels);
    GB_set_border_mode(session->gb, GB_BORDER_NEVER);
}

static void write_post_boot_io(GB_gameboy_t *gb, uint16_t address, uint8_t value)
{
    GB_write_memory(gb, address, value);
}

static void apply_post_boot_io_state(GB_gameboy_t *gb)
{
    write_post_boot_io(gb, 0xFF05, 0x00);
    write_post_boot_io(gb, 0xFF06, 0x00);
    write_post_boot_io(gb, 0xFF07, 0x00);
    write_post_boot_io(gb, 0xFF10, 0x80);
    write_post_boot_io(gb, 0xFF11, 0xBF);
    write_post_boot_io(gb, 0xFF12, 0xF3);
    write_post_boot_io(gb, 0xFF14, 0xBF);
    write_post_boot_io(gb, 0xFF16, 0x3F);
    write_post_boot_io(gb, 0xFF17, 0x00);
    write_post_boot_io(gb, 0xFF19, 0xBF);
    write_post_boot_io(gb, 0xFF1A, 0x7F);
    write_post_boot_io(gb, 0xFF1B, 0xFF);
    write_post_boot_io(gb, 0xFF1C, 0x9F);
    write_post_boot_io(gb, 0xFF1E, 0xBF);
    write_post_boot_io(gb, 0xFF20, 0xFF);
    write_post_boot_io(gb, 0xFF21, 0x00);
    write_post_boot_io(gb, 0xFF22, 0x00);
    write_post_boot_io(gb, 0xFF23, 0xBF);
    write_post_boot_io(gb, 0xFF24, 0x77);
    write_post_boot_io(gb, 0xFF25, 0xF3);
    write_post_boot_io(gb, 0xFF26, 0xF1);
    write_post_boot_io(gb, 0xFF40, 0x91);
    write_post_boot_io(gb, 0xFF42, 0x00);
    write_post_boot_io(gb, 0xFF43, 0x00);
    write_post_boot_io(gb, 0xFF45, 0x00);
    write_post_boot_io(gb, 0xFF47, 0xFC);
    write_post_boot_io(gb, 0xFF48, 0xFF);
    write_post_boot_io(gb, 0xFF49, 0xFF);
    write_post_boot_io(gb, 0xFF4A, 0x00);
    write_post_boot_io(gb, 0xFF4B, 0x00);
}

static void apply_post_boot_state(gbmcp_session_t *session)
{
    GB_gameboy_t *gb = session->gb;
    gb->boot_rom_finished = true;
    gb->sp = 0xFFFE;
    gb->pc = 0x0100;
    gb->ime = false;
    gb->halted = false;
    gb->stopped = false;

    if (GB_is_cgb(gb)) {
        gb->af = 0x1180;
        gb->bc = 0x0000;
        gb->de = 0xFF56;
        gb->hl = 0x000D;
    }
    else {
        gb->af = 0x01B0;
        gb->bc = 0x0013;
        gb->de = 0x00D8;
        gb->hl = 0x014D;
    }

    apply_post_boot_io_state(gb);
    GB_write_memory(gb, 0xFF50, 1);
}

static bool detect_cgb_rom(const char *path)
{
    FILE *file = fopen(path, "rb");
    if (!file) return false;

    if (fseek(file, 0x143, SEEK_SET) != 0) {
        fclose(file);
        return false;
    }

    int flag = fgetc(file);
    fclose(file);
    return flag == 0x80 || flag == 0xC0;
}

GBMCP_EXPORT gbmcp_session_t *gbmcp_create(void)
{
    gbmcp_session_t *session = calloc(1, sizeof(*session));
    if (!session) return NULL;

    session->active_model = GB_MODEL_DMG_B;
    session->gb = GB_alloc();
    if (!session->gb) {
        free(session);
        return NULL;
    }

    GB_init(session->gb, session->active_model);
    configure_core(session);
    apply_post_boot_state(session);
    return session;
}

GBMCP_EXPORT void gbmcp_destroy(gbmcp_session_t *session)
{
    if (!session) return;
    if (session->gb) {
        GB_free(session->gb);
        GB_dealloc(session->gb);
    }

    free(session->log_buffer);
    free(session);
}

GBMCP_EXPORT int gbmcp_get_last_error(gbmcp_session_t *session, char *buffer, size_t buffer_length)
{
    if (!session || !buffer || buffer_length == 0) return -1;
    snprintf(buffer, buffer_length, "%s", session->last_error);
    return 0;
}

GBMCP_EXPORT int gbmcp_load_rom(gbmcp_session_t *session, const char *path, char *title, size_t title_length, char *model, size_t model_length)
{
    if (!session || !path) return -1;

    GB_model_t desired_model = detect_cgb_rom(path) ? GB_MODEL_CGB_E : GB_MODEL_DMG_B;
    if (desired_model != session->active_model) {
        GB_switch_model_and_reset(session->gb, desired_model);
        session->active_model = desired_model;
        configure_core(session);
    }

    int result = GB_load_rom(session->gb, path);
    if (result != 0) {
        set_error(session, "SameBoy could not load the ROM.");
        return result;
    }

    GB_reset(session->gb);
    apply_post_boot_state(session);
    session->loaded = true;

    if (title && title_length > 0) {
        char rom_title[17];
        GB_get_rom_title(session->gb, rom_title);
        snprintf(title, title_length, "%s", rom_title);
    }

    if (model && model_length > 0) {
        snprintf(model, model_length, "%s", GB_is_cgb(session->gb) ? "CGB" : "DMG");
    }

    return 0;
}

GBMCP_EXPORT int gbmcp_reset(gbmcp_session_t *session)
{
    if (!session || !session->loaded) return -1;
    GB_reset(session->gb);
    apply_post_boot_state(session);
    return 0;
}

GBMCP_EXPORT int gbmcp_step(gbmcp_session_t *session)
{
    if (!session || !session->loaded) return -1;
    GB_run(session->gb);
    return 0;
}

GBMCP_EXPORT int gbmcp_run_frame(gbmcp_session_t *session)
{
    if (!session || !session->loaded) return -1;
    GB_run_frame(session->gb);
    return 0;
}

GBMCP_EXPORT int gbmcp_read_registers(gbmcp_session_t *session, gbmcp_registers_t *registers)
{
    if (!session || !registers) return -1;

    registers->af = session->gb->af;
    registers->bc = session->gb->bc;
    registers->de = session->gb->de;
    registers->hl = session->gb->hl;
    registers->sp = session->gb->sp;
    registers->pc = session->gb->pc;
    registers->a = session->gb->a;
    registers->f = session->gb->f;
    registers->b = session->gb->b;
    registers->c = session->gb->c;
    registers->d = session->gb->d;
    registers->e = session->gb->e;
    registers->h = session->gb->h;
    registers->l = session->gb->l;
    registers->ime = session->gb->ime;
    registers->halted = session->gb->halted;
    return 0;
}

GBMCP_EXPORT int gbmcp_read_memory(gbmcp_session_t *session, uint16_t address, uint8_t *buffer, size_t length)
{
    if (!session || !buffer) return -1;
    for (size_t i = 0; i < length; i++) {
        buffer[i] = GB_safe_read_memory(session->gb, (uint16_t)(address + i));
    }

    return 0;
}

GBMCP_EXPORT int gbmcp_write_memory(gbmcp_session_t *session, uint16_t address, const uint8_t *buffer, size_t length)
{
    if (!session || !buffer) return -1;
    for (size_t i = 0; i < length; i++) {
        GB_write_memory(session->gb, (uint16_t)(address + i), buffer[i]);
    }

    return 0;
}

GBMCP_EXPORT int gbmcp_disassemble(gbmcp_session_t *session, uint16_t address, uint16_t count, char *buffer, size_t buffer_length)
{
    if (!session || !buffer || buffer_length == 0) return -1;

    session->log_length = 0;
    if (session->log_buffer) {
        session->log_buffer[0] = 0;
    }

    session->capture_log = true;
    GB_cpu_disassemble(session->gb, address, count);
    session->capture_log = false;

    snprintf(buffer, buffer_length, "%s", session->log_buffer ? session->log_buffer : "");
    return 0;
}

GBMCP_EXPORT int gbmcp_read_oam(gbmcp_session_t *session, uint8_t *buffer, size_t length)
{
    if (!session || !buffer || length < 0xA0) return -1;

    size_t size = 0;
    uint8_t *oam = GB_get_direct_access(session->gb, GB_DIRECT_ACCESS_OAM, &size, NULL);
    if (!oam || size < 0xA0) return -1;
    memcpy(buffer, oam, 0xA0);
    return 0;
}

GBMCP_EXPORT int gbmcp_capture_screen(gbmcp_session_t *session, uint32_t *buffer, size_t length)
{
    if (!session || !buffer || length < GBMCP_PIXELS) return -1;
    memcpy(buffer, session->pixels, sizeof(session->pixels));
    return 0;
}

GBMCP_EXPORT int gbmcp_get_last_writer(gbmcp_session_t *session, uint16_t address, uint16_t *pc, uint8_t *value, uint64_t *count)
{
    if (!session || !pc || !value || !count) return -1;

    gbmcp_last_write_t last_write = session->last_writes[address];
    if (!last_write.found) {
        *pc = 0;
        *value = 0;
        *count = 0;
        return 1;
    }

    *pc = last_write.pc;
    *value = last_write.value;
    *count = last_write.count;
    return 0;
}

GBMCP_EXPORT int gbmcp_trace_until_write(gbmcp_session_t *session, uint16_t address, uint32_t max_instructions, uint32_t *instructions_run, uint16_t *pc, uint8_t *value)
{
    if (!session || !instructions_run || !pc || !value) return -1;
    if (!session->loaded) return -1;

    session->trace_active = true;
    session->trace_hit = false;
    session->trace_address = address;
    session->trace_pc = 0;
    session->trace_value = 0;

    *instructions_run = 0;
    for (uint32_t i = 0; i < max_instructions; i++) {
        GB_run(session->gb);
        *instructions_run = i + 1;
        if (session->trace_hit) {
            break;
        }
    }

    session->trace_active = false;
    *pc = session->trace_pc;
    *value = session->trace_value;
    return session->trace_hit ? 0 : 1;
}
