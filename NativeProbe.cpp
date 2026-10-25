#define NOMINMAX
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <share.h>
#include <set>
#include <unordered_map>
#include <algorithm>
#include <climits>
#include <vector>
#include "NativeCheckpoint.hpp"

// ReplayLab probe protocol v8. All offsets below are for the verified th123.exe 1.10a.
// This DLL must be initialized only after the host validates the executable hash.
#pragma pack(push, 4)
struct ProbeChannel {
    volatile LONG magic, version;
    volatile LONG sequence, acknowledged, command, status;
    volatile LONG ordinal, target, phase, engine_frame;
    char replay_path[520];
    char trace_path[520];
};
#pragma pack(pop)
static_assert(sizeof(ProbeChannel) == 1080, "Probe protocol size changed");

namespace {
constexpr LONG kMagic = 0x524c5031; // RLP1
constexpr LONG kVersion = 8;
constexpr LONG kLoad = 1, kPlay = 2, kPause = 3, kSeek = 4, kRelease = 5,
    kSpeed = 6, kSaveSnapshot = 7, kRestoreSnapshot = 8, kCapture = 9,
    kBranch = 10, kRestoreState = 11, kFreeControl = 12;
constexpr LONG kIdle = 0, kLoading = 1, kPlaying = 2, kPaused = 3;
using Scene = int(__thiscall *)(void *);
using SceneRender = bool(__thiscall *)(void *);
Scene original_logo = nullptr;
Scene original_title = nullptr;
Scene original_battle = nullptr;
SceneRender original_battle_render = nullptr;
HANDLE mapping = nullptr;
ProbeChannel *channel = nullptr;
FILE *trace = nullptr;
bool initialized = false;
bool replay_active = false;
bool restart_seek = false;
bool simulation_trace = false;
LONG trace_start_ordinal = 0;
LONG seek_target = -1;
LONG speed_percent = 100;
LONG speed_step_phase = 0;
uint32_t previous_frame = 0xffffffffu;
char active_replay[520] = {};
char active_trace[520] = {};
native_checkpoint::Store snapshots;
std::set<LONG> automatic_slots;
std::vector<uint16_t> branch_inputs;
LONG branch_anchor = -1;
unsigned branch_side = 0;
LONG branch_hook_calls = 0;
LONG branch_applied = 0;
using KeyUpdate = void(__thiscall *)(void *);
std::unordered_map<uintptr_t, KeyUpdate> key_updates;
std::vector<uintptr_t> replay_buffers;
std::vector<uintptr_t> channel_buffers[2];
struct InputPatch { uintptr_t address; uintptr_t stride; std::vector<uint16_t> old; };
std::vector<InputPatch> input_patches;
HWND cursor_window = nullptr;
HWND cursor_target = nullptr;
WNDPROC original_window_proc = nullptr;
bool cursor_window_unicode = false;
ULONGLONG next_cursor_attempt = 0;
bool configure_keyboard_focus(HWND window, bool enabled);

bool embedded_window(HWND window) {
    return (GetWindowLongPtrW(window, GWL_STYLE) & WS_CHILD) != 0 &&
        GetParent(window) != nullptr;
}

LRESULT call_original_window_proc(HWND window, UINT message, WPARAM w, LPARAM l) {
    return cursor_window_unicode
        ? CallWindowProcW(original_window_proc, window, message, w, l)
        : CallWindowProcA(original_window_proc, window, message, w, l);
}

LRESULT CALLBACK visible_cursor_proc(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_APP + 0x524)
        return configure_keyboard_focus(window, w != 0) ? 1 : 0;
    if (embedded_window(window) && message == WM_SETCURSOR && LOWORD(l) == HTCLIENT) {
        CURSORINFO info = {}; info.cbSize = sizeof(info);
        if (GetCursorInfo(&info) && !(info.flags & CURSOR_SHOWING)) {
            for (int i = 0; i < 64 && ShowCursor(TRUE) < 0; ++i) {}
        }
        SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(32512)));
        return TRUE;
    }
    auto result = call_original_window_proc(window, message, w, l);
    if (embedded_window(window) && message == WM_NCHITTEST &&
        result == HTTRANSPARENT) return HTCLIENT;
    return result;
}

bool install_visible_cursor(HWND window) {
    DWORD owner = 0;
    if (!window || !IsWindow(window) || !GetWindowThreadProcessId(window, &owner) ||
        owner != GetCurrentProcessId()) return false;
    if (cursor_window == window && original_window_proc &&
        GetWindowLongPtrW(window, GWLP_WNDPROC) ==
            reinterpret_cast<LONG_PTR>(visible_cursor_proc)) return true;
    if (cursor_window && IsWindow(cursor_window)) return false;
    cursor_window_unicode = IsWindowUnicode(window) != FALSE;
    // The window can receive a message as soon as SetWindowLongPtr installs
    // our procedure. Publish the fallback before making that change.
    original_window_proc = reinterpret_cast<WNDPROC>(
        GetWindowLongPtrW(window, GWLP_WNDPROC));
    if (!original_window_proc) return false;
    SetLastError(0);
    auto previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window, GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(visible_cursor_proc)));
    if (!previous && GetLastError()) {
        original_window_proc = nullptr;
        return false;
    }
    cursor_window = window;
    if (previous) original_window_proc = previous;
    return true;
}

void retry_visible_cursor() {
    if (!cursor_target || cursor_window == cursor_target) return;
    const auto now = GetTickCount64();
    if (now < next_cursor_attempt) return;
    next_cursor_attempt = now + 1000;
    install_visible_cursor(cursor_target);
}

bool readable(uintptr_t address, size_t bytes) {
    if (address < 0x10000 || address > 0x7fff0000 || bytes > 0x10000) return false;
    MEMORY_BASIC_INFORMATION info = {};
    if (!VirtualQuery(reinterpret_cast<void *>(address), &info, sizeof(info))) return false;
    const auto base = reinterpret_cast<uintptr_t>(info.BaseAddress);
    return info.State == MEM_COMMIT && !(info.Protect & (PAGE_GUARD | PAGE_NOACCESS)) &&
        address + bytes >= address && address + bytes <= base + info.RegionSize;
}
template <typename T> T value(uintptr_t address) {
    return readable(address, sizeof(T)) ? *reinterpret_cast<const T *>(address) : T{};
}
// The caller has already validated the whole battle/player structure.
template <typename T> T known(uintptr_t address) {
    return *reinterpret_cast<const T *>(address);
}
bool configure_keyboard_focus(HWND window, bool enabled) {
    // DirectInput foreground acquisition assumes a top-level game HWND.
    // While embedded, change only acquisition, never key mappings or buffers.
    const auto device = value<uint32_t>(0x008A01A0);
    // This is the game's window-activation gate, not a key input buffer.
    // A cross-process child does not consistently get WM_ACTIVATEAPP.
    *reinterpret_cast<uint8_t *>(0x0089FF90 + 0x29) = 0;
    const auto table = value<uint32_t>(device);
    if (!readable(table, 0x38)) return false;
    using Acquire = HRESULT(WINAPI *)(void *);
    using Cooperative = HRESULT(WINAPI *)(void *, HWND, DWORD);
    auto acquire = reinterpret_cast<Acquire>(value<uint32_t>(table + 0x1c));
    auto unacquire = reinterpret_cast<Acquire>(value<uint32_t>(table + 0x20));
    auto cooperative = reinterpret_cast<Cooperative>(value<uint32_t>(table + 0x34));
    unacquire(reinterpret_cast<void *>(device));
    const auto result = cooperative(reinterpret_cast<void *>(device), enabled ? nullptr : window,
        enabled ? 0x0a : 0x16);
    if (FAILED(result)) return false;
    const auto acquired = acquire(reinterpret_cast<void *>(device));
    if (enabled && SUCCEEDED(acquired))
        *reinterpret_cast<uint8_t *>(0x0089FF90 + 0x29) = 1;
    return !enabled || SUCCEEDED(acquired);
}
void close_trace() {
    if (trace) { std::fflush(trace); std::fclose(trace); trace = nullptr; }
}
void restore_pacing() { *reinterpret_cast<volatile int *>(0x008A0FF8) = 16; }

void open_trace() {
    close_trace();
    if (!active_trace[0]) return;
    trace = _fsopen(active_trace, "wb", _SH_DENYNO);
    if (!trace) return;
    std::setvbuf(trace, nullptr, _IOFBF, 1 << 20);
    if (simulation_trace) {
        std::fputs("frame,match_state,round,weather,"
            "p1_char,p1_x,p1_y,p1_action,p1_action_frame,p1_hp,p1_spirit,p1_input,"
            "p1_combo_damage,p1_card_count,p1_card_gauge,p1_hit_stop,p1_collision_type,p1_hand_ids,p1_used_ids,"
            "p2_char,p2_x,p2_y,p2_action,p2_action_frame,p2_hp,p2_spirit,p2_input,"
            "p2_combo_damage,p2_card_count,p2_card_gauge,p2_hit_stop,p2_collision_type,p2_hand_ids,p2_used_ids\n", trace);
        std::fflush(trace);
        return;
    }
    std::fputs("frame,match_state,round,weather,"
        "p1_char,p1_x,p1_y,p1_action,p1_action_frame,p1_hp,p1_spirit,p1_input,"
        "p1_combo_count,p1_combo_damage,p1_combo_limit,p1_card_count,p1_card_gauge,"
        "p1_hit_stop,p1_collision_type,p1_damage_limited,p1_frame_flags,p1_hand_ids,p1_used_ids,"
        "p1_dir,p1_max_spirit,p1_broken_orb,p1_card_selected,p1_charged_attack,p1_attack_flags,p1_counter_vulnerable,p1_untech,p1_active_melee_reach,p1_melee_max_reach,"
        "p2_char,p2_x,p2_y,p2_action,p2_action_frame,p2_hp,p2_spirit,p2_input,"
        "p2_combo_count,p2_combo_damage,p2_combo_limit,p2_card_count,p2_card_gauge,"
        "p2_hit_stop,p2_collision_type,p2_damage_limited,p2_frame_flags,p2_hand_ids,p2_used_ids,"
        "p2_dir,p2_max_spirit,p2_broken_orb,p2_card_selected,p2_charged_attack,p2_attack_flags,p2_counter_vulnerable,p2_untech,p2_active_melee_reach,p2_melee_max_reach\n", trace);
    std::fflush(trace);
}

void print_cards(uintptr_t player, unsigned offset, bool hand) {
    const uintptr_t deque = player + offset;
    const uintptr_t table = known<uint32_t>(deque + 4);
    const unsigned blocks = known<uint32_t>(deque + 8);
    const unsigned start = known<uint32_t>(deque + 12);
    const unsigned count = known<uint32_t>(deque + 16);
    const unsigned per_block = hand ? 1 : 8;
    if (count > (hand ? 5u : 20u) || blocks > 256 ||
        (count && (!blocks || !table || !readable(table, blocks * 4)))) {
        std::fputc('?', trace); return;
    }
    if (!count) return;
    const unsigned capacity = blocks * per_block;
    if (start >= capacity) { std::fputc('?', trace); return; }
    uint16_t ids[20] = {};
    for (unsigned i = 0; i < count; ++i) {
        const unsigned position = (start + i) % capacity;
        const uintptr_t block = value<uint32_t>(table + (position / per_block) * 4);
        const uintptr_t card = block + (hand ? 0 : position % per_block * 2);
        if (!readable(card, 2)) { std::fputc('?', trace); return; }
        ids[i] = value<uint16_t>(card);
        if (!(ids[i] <= 21 || (ids[i] >= 100 && ids[i] <= 114) ||
              (ids[i] >= 200 && ids[i] <= 219))) {
            std::fputc('?', trace); return;
        }
    }
    for (unsigned i = 0; i < count; ++i)
        std::fprintf(trace, "%s%u", i ? "|" : "", ids[i]);
}

int active_melee_reach(uintptr_t player) {
    const uintptr_t animation = known<uint32_t>(player + 0x158);
    if (!readable(animation + 0x70, 4)) return -1;
    const uintptr_t first = value<uint32_t>(animation + 0x6c);
    const uintptr_t end = value<uint32_t>(animation + 0x70);
    if (end < first || end - first > 80 || (end - first) % 16) return -1;
    if (end > first && !readable(first, end - first)) return -1;
    int reach = 0;
    for (auto box = first; box < end; box += 16) {
        const int left = value<int32_t>(box), right = value<int32_t>(box + 8);
        if (left >= -2000 && right <= 2000 && left < right && right > reach)
            reach = right;
    }
    return reach;
}

int fixed_melee_reach(uintptr_t player) {
    const unsigned action = known<uint16_t>(player + 0x13c);
    if (action < 300 || action >= 600) return 0;
    const uintptr_t sequence = known<uint32_t>(player + 0x1c4);
    if (!readable(sequence, 0x20)) return 0;
    static std::unordered_map<uint64_t, int> cache;
    const uint64_t key = (static_cast<uint64_t>(known<uint8_t>(player + 0x34c)) << 48) |
        (static_cast<uint64_t>(action) << 32) | sequence;
    const auto hit = cache.find(key);
    if (hit != cache.end()) return hit->second;
    uintptr_t first = sequence;
    for (int i = 0; i < 24 && readable(first, 0x20); ++i) {
        const uintptr_t previous = value<uint32_t>(first + 0x18);
        if (!previous || previous == first || !readable(previous, 0x20)) break;
        first = previous;
    }
    int reach = 0;
    uintptr_t current = first;
    for (int i = 0; i < 48 && readable(current, 0x20); ++i) {
        const uintptr_t begin = value<uint32_t>(current + 4);
        const uintptr_t end = value<uint32_t>(current + 8);
        if (end >= begin && end - begin <= 256 * 0xA8 &&
            (end - begin) % 0xA8 == 0 && (end == begin || readable(begin, end - begin))) {
            for (auto frame = begin; frame < end; frame += 0xA8) {
                const uintptr_t boxes = value<uint32_t>(frame + 0x6c);
                const uintptr_t boxesEnd = value<uint32_t>(frame + 0x70);
                if (boxesEnd < boxes || boxesEnd - boxes > 80 || (boxesEnd - boxes) % 16 ||
                    (boxesEnd > boxes && !readable(boxes, boxesEnd - boxes))) continue;
                for (auto box = boxes; box < boxesEnd; box += 16) {
                    const int left = value<int32_t>(box), right = value<int32_t>(box + 8);
                    if (left >= -2000 && right <= 2000 && left < right && right > reach)
                        reach = right;
                }
            }
        }
        const uintptr_t next = value<uint32_t>(current + 0x1c);
        if (!next || next == first || next == current) break;
        current = next;
    }
    if (reach > 0) cache[key] = reach;
    return reach;
}

void print_player(uintptr_t player) {
    const uintptr_t key_owner = known<uint32_t>(player + 0x750);
    const uintptr_t key_state = value<uint32_t>(key_owner);
    const uintptr_t animation = known<uint32_t>(player + 0x158);
    const uintptr_t counter_frame = known<uint32_t>(player + 0x1c0);
    const int flags = readable(animation + 0x4c, 4) ? value<int>(animation + 0x4c) : -1;
    const int attack_flags = readable(animation + 0x50, 4) ? value<int>(animation + 0x50) : -1;
    const int counter = readable(counter_frame + 0x4c, 4)
        ? (value<uint32_t>(counter_frame + 0x4c) & 0x40) != 0 : -1;
    std::fprintf(trace, ",%u,%.3f,%.3f,%u,%u,%d,%u,%u,%u,%u,%u,%u,%u,%u,%u,%d,%d",
        known<uint8_t>(player + 0x34c), known<float>(player + 0xec),
        known<float>(player + 0xf0), known<uint16_t>(player + 0x13c),
        known<uint16_t>(player + 0x4be), known<int16_t>(player + 0x184),
        known<uint16_t>(player + 0x49e), value<uint16_t>(key_state + 0x60),
        known<uint16_t>(player + 0x4b4), known<uint16_t>(player + 0x4b6),
        known<uint16_t>(player + 0x4b8), known<uint8_t>(player + 0x5e6),
        known<uint16_t>(player + 0x5e4), known<uint16_t>(player + 0x196),
        known<uint8_t>(player + 0x7f7), known<int32_t>(player + 0x190), flags);
    std::fputc(',', trace); print_cards(player, 0x5e8, true);
    std::fputc(',', trace); print_cards(player, 0x5fc, false);
    std::fprintf(trace, ",%d,%u,%u,%u,%d,%d,%d,%u,%d,%d",
        static_cast<int>(known<int8_t>(player + 0x104)),
        known<uint16_t>(player + 0x4a0), known<uint16_t>(player + 0x4ba),
        known<uint8_t>(player + 0x7f7),
        static_cast<int>(known<uint8_t>(player + 0x7f4) != 0),
        attack_flags, counter, known<uint32_t>(player + 0x144),
        active_melee_reach(player), fixed_melee_reach(player));
}

void print_simulation_player(uintptr_t player) {
    const uintptr_t key_owner = known<uint32_t>(player + 0x750);
    const uintptr_t key_state = value<uint32_t>(key_owner);
    std::fprintf(trace, ",%u,%.3f,%.3f,%u,%u,%d,%u,%u,%u,%u,%u,%u,%u",
        known<uint8_t>(player + 0x34c), known<float>(player + 0xec),
        known<float>(player + 0xf0), known<uint16_t>(player + 0x13c),
        known<uint16_t>(player + 0x4be), known<int16_t>(player + 0x184),
        known<uint16_t>(player + 0x49e), value<uint16_t>(key_state + 0x60),
        known<uint16_t>(player + 0x4b6), known<uint8_t>(player + 0x5e6),
        known<uint16_t>(player + 0x5e4), known<uint16_t>(player + 0x196),
        known<uint8_t>(player + 0x7f7));
    std::fputc(',', trace); print_cards(player, 0x5e8, true);
    std::fputc(',', trace); print_cards(player, 0x5fc, false);
}

void capture() {
    const uintptr_t battle = value<uint32_t>(0x008985E4);
    if (!readable(battle, 0x908)) return;
    const uint32_t frame = known<uint32_t>(battle + 4);
    if (frame == previous_frame) return;
    previous_frame = frame;
    ++channel->ordinal;
    channel->engine_frame = static_cast<LONG>(frame);
    channel->phase = known<int32_t>(battle + 0x88);
    if (!trace || channel->ordinal < trace_start_ordinal) return;
    const uintptr_t left = known<uint32_t>(battle + 0xc);
    const uintptr_t right = known<uint32_t>(battle + 0x10);
    if (!readable(left, 0x800) || !readable(right, 0x800)) return;
    std::fprintf(trace, "%u,%d,%d,%u", frame, channel->phase,
        known<int8_t>(battle + 0x904), value<uint32_t>(0x008971C0));
    if (simulation_trace) {
        print_simulation_player(left);
        print_simulation_player(right);
    } else {
        print_player(left);
        print_player(right);
    }
    std::fputc('\n', trace);
    if ((channel->ordinal & 63) == 0 &&
        !(channel->status == kLoading && channel->command == kCapture))
        std::fflush(trace);
    if (!simulation_trace && channel->ordinal > 0 &&
        channel->ordinal % 600 == 0 &&
        snapshots.count() < native_checkpoint::MaxImages &&
        (!snapshots.contains(channel->ordinal) ||
         automatic_slots.count(static_cast<LONG>(channel->ordinal)) != 0) &&
        snapshots.initialize())
        if (snapshots.save(channel->ordinal, channel->ordinal,
            channel->engine_frame, channel->phase, trace))
            automatic_slots.insert(static_cast<LONG>(channel->ordinal));
}

// Use the game's streaming-music pause/resume methods. Stopping only the
// battle update leaves DirectSound and the decoder thread running. State 3
// preserves the playback cursor; volume changes would merely hide progression.
bool music_held = false;
uint32_t held_music = 0;
void sync_music(bool hold) {
    if (!hold && !music_held) return;
    auto manager = reinterpret_cast<void *>(0x0089FE50);
    auto lock = reinterpret_cast<CRITICAL_SECTION *>(0x0089FE70);
    auto lookup = reinterpret_cast<uintptr_t(__thiscall *)(void *, uint32_t)>(0x004045F0);
    auto pause = reinterpret_cast<void(__thiscall *)(void *, uint32_t)>(0x00403BB0);
    auto resume = reinterpret_cast<void(__thiscall *)(void *, uint32_t, int)>(0x00403B20);
    EnterCriticalSection(lock);
    const auto current = *reinterpret_cast<uint32_t *>(0x00899D5C);
    if (music_held && (!hold || current != held_music)) {
        const auto old = lookup(reinterpret_cast<void *>(0x0089FE88), held_music);
        // Do not undo a stop or a track change performed by the game itself.
        if (current == held_music && old && *reinterpret_cast<int *>(old + 0x1C) == 3)
            resume(manager, held_music, 0);
        music_held = false;
    }
    if (hold && !music_held) {
        const auto bgm = lookup(reinterpret_cast<void *>(0x0089FE88), current);
        if (bgm && *reinterpret_cast<int *>(bgm + 0x1C) == 0 &&
            *reinterpret_cast<uint8_t *>(bgm + 0x20)) {
            pause(manager, current);
            held_music = current;
            music_held = true;
        }
    }
    LeaveCriticalSection(lock);
}

void sync_replay_music() {
    sync_music(channel && replay_active && channel->status != kPlaying &&
        channel->status != kIdle);
}

void acknowledge(LONG status) {
    channel->status = status;
    sync_replay_music();
    MemoryBarrier();
    channel->acknowledged = channel->sequence;
}

void restore_input_patches() {
    for (const auto &patch : input_patches)
        for (size_t i = 0; i < patch.old.size(); ++i)
            *reinterpret_cast<uint16_t *>(patch.address + i * patch.stride) = patch.old[i];
    input_patches.clear();
}

bool find_replay_buffers() {
    if (!replay_buffers.empty()) return true;
    FILE *file = nullptr;
    if (fopen_s(&file, active_replay, "rb") || !file) return false;
    std::fseek(file, 0, SEEK_END);
    const long size = std::ftell(file);
    if (size < 1200 || size > 2000000 || std::fseek(file, 0, SEEK_SET)) {
        std::fclose(file); return false;
    }
    std::vector<uint8_t> raw(size);
    const bool read = std::fread(raw.data(), 1, size, file) == static_cast<size_t>(size);
    std::fclose(file);
    if (!read) return false;
    const auto word = [&](size_t offset) -> uint32_t {
        uint32_t result = 0;
        if (offset + 4 <= raw.size()) std::memcpy(&result, raw.data() + offset, 4);
        return result;
    };
    const auto first = word(16);
    if (first > 100) return false;
    const size_t second_offset = 20 + first * 2;
    const auto second = word(second_offset + 5);
    if (second > 100) return false;
    const size_t payload = second_offset + 9 + second * 2 + 14;
    constexpr size_t first_sample = 400, second_sample = 800, sample_size = 128;
    if (payload + second_sample + sample_size > raw.size()) return false;
    MEMORY_BASIC_INFORMATION info = {};
    for (uintptr_t address = 0x10000; address < 0x7fff0000;) {
        if (!VirtualQuery(reinterpret_cast<void *>(address), &info, sizeof(info))) break;
        const auto start = reinterpret_cast<uintptr_t>(info.BaseAddress);
        const auto end = start + info.RegionSize;
        if (end <= address) break;
        const auto protect = info.Protect & 0xff;
        const bool writable = protect == PAGE_READWRITE || protect == PAGE_EXECUTE_READWRITE ||
            protect == PAGE_WRITECOPY || protect == PAGE_EXECUTE_WRITECOPY;
        if (info.State == MEM_COMMIT && writable && !(info.Protect & PAGE_GUARD) &&
            end - start >= second_sample + sample_size) {
            auto cursor = reinterpret_cast<const uint8_t *>(start);
            const auto limit = reinterpret_cast<const uint8_t *>(end - sample_size);
            while (cursor <= limit) {
                cursor = static_cast<const uint8_t *>(std::memchr(cursor,
                    raw[payload + first_sample], limit - cursor + 1));
                if (!cursor) break;
                const auto hit = reinterpret_cast<uintptr_t>(cursor);
                const auto base = hit - first_sample;
                const auto own_begin = reinterpret_cast<uintptr_t>(raw.data());
                const auto own_end = own_begin + raw.size();
                if (hit >= start + first_sample && base + second_sample + sample_size <= end &&
                    (base < own_begin || base >= own_end) &&
                    std::memcmp(cursor, raw.data() + payload + first_sample, sample_size) == 0 &&
                    std::memcmp(reinterpret_cast<const void *>(base + second_sample),
                        raw.data() + payload + second_sample, sample_size) == 0)
                    replay_buffers.push_back(base);
                ++cursor;
                if (replay_buffers.size() > 8) return false;
            }
        }
        address = end;
    }
    for (unsigned side = 0; side < 2; ++side) {
        uint8_t sample[64] = {}, verify[64] = {};
        for (size_t i = 0; i < 32; ++i) {
            std::memcpy(sample + i * 2,
                raw.data() + payload + (100 + i) * 4 + side * 2, 2);
            std::memcpy(verify + i * 2,
                raw.data() + payload + (200 + i) * 4 + side * 2, 2);
        }
        for (uintptr_t address = 0x10000; address < 0x7fff0000;) {
            if (!VirtualQuery(reinterpret_cast<void *>(address), &info, sizeof(info))) break;
            const auto start = reinterpret_cast<uintptr_t>(info.BaseAddress);
            const auto end = start + info.RegionSize;
            if (end <= address) break;
            const auto protect = info.Protect & 0xff;
            const bool writable = protect == PAGE_READWRITE || protect == PAGE_EXECUTE_READWRITE ||
                protect == PAGE_WRITECOPY || protect == PAGE_EXECUTE_WRITECOPY;
            if (info.State == MEM_COMMIT && writable && !(info.Protect & PAGE_GUARD) &&
                end - start >= 464) {
                auto cursor = reinterpret_cast<const uint8_t *>(start);
                const auto limit = reinterpret_cast<const uint8_t *>(end - 64);
                while (cursor <= limit) {
                    cursor = static_cast<const uint8_t *>(std::memchr(cursor,
                        sample[0], limit - cursor + 1));
                    if (!cursor) break;
                    const auto hit = reinterpret_cast<uintptr_t>(cursor);
                    const auto base = hit - 200;
                    if (hit >= start + 200 && base + 464 <= end &&
                        std::memcmp(cursor, sample, 64) == 0 &&
                        std::memcmp(reinterpret_cast<const void *>(base + 400), verify, 64) == 0)
                        channel_buffers[side].push_back(base);
                    ++cursor;
                    if (channel_buffers[side].size() > 8) return false;
                }
            }
            address = end;
        }
    }
    return !replay_buffers.empty() || !channel_buffers[0].empty() ||
        !channel_buffers[1].empty();
}

bool patch_replay_inputs(uint32_t input_index, unsigned side,
                         const std::vector<uint16_t> &masks) {
    restore_input_patches();
    // 1.10a's current round replay stream is an MSVC deque<ushort>, eight
    // values per block, not a contiguous copy of the REP payload.
    const uintptr_t round = value<uint32_t>(0x00898718 + 0x104);
    if (!readable(round, 0x50)) return false;
    const uintptr_t queue = round + 0x3c;
    const auto table = value<uint32_t>(queue + 4);
    const auto blocks = value<uint32_t>(queue + 8);
    const auto head = value<uint32_t>(queue + 12);
    const auto count = value<uint32_t>(queue + 16);
    if (side > 1 || !blocks || blocks > 16384 || count < masks.size() * 2 ||
        !readable(table, blocks * 4)) return false;
    auto element = [&](size_t index) -> uintptr_t {
        const size_t position = head + index;
        const uintptr_t block = value<uint32_t>(table + ((position / 8) % blocks) * 4);
        const uintptr_t address = block + (position % 8) * 2;
        return readable(address, 2) ? address : 0;
    };
    FILE *file = nullptr;
    if (fopen_s(&file, active_replay, "rb") || !file) return false;
    std::fseek(file, 0, SEEK_END);
    const long size = std::ftell(file);
    std::rewind(file);
    if (size < 64 || size > 2000000) { std::fclose(file); return false; }
    std::vector<uint8_t> raw(size);
    const bool loaded = std::fread(raw.data(), 1, size, file) == static_cast<size_t>(size);
    std::fclose(file);
    if (!loaded) return false;
    auto word = [&](size_t offset) -> uint32_t {
        uint32_t result = 0;
        if (offset + 4 <= raw.size()) std::memcpy(&result, raw.data() + offset, 4);
        return result;
    };
    const auto first = word(16);
    if (first > 100) return false;
    const size_t second_offset = 20 + first * 2;
    const auto second = word(second_offset + 5);
    if (second > 100) return false;
    const size_t payload = second_offset + 9 + second * 2 + 14 + input_index * 4;
    if (payload + masks.size() * 4 > raw.size()) return false;
    // Validate the entire pending window against the aligned original REP
    // before any write. An offset or unsupported queue layout is rejected.
    for (size_t index = 0; index < masks.size() * 2; ++index) {
        const auto address = element(index);
        uint16_t expected = 0;
        std::memcpy(&expected, raw.data() + payload + index * 2, 2);
        if (!address || value<uint16_t>(address) != expected) return false;
        if (index % 2 == side) {
            MEMORY_BASIC_INFORMATION info = {};
            if (!VirtualQuery(reinterpret_cast<void *>(address), &info, sizeof(info))) return false;
            const DWORD protection = info.Protect & 0xff;
            if (protection != PAGE_READWRITE && protection != PAGE_WRITECOPY &&
                protection != PAGE_EXECUTE_READWRITE && protection != PAGE_EXECUTE_WRITECOPY)
                return false;
        }
    }
    for (size_t index = 0; index < masks.size(); ++index) {
        const auto address = element(index * 2 + side);
        InputPatch patch { address, 0, { value<uint16_t>(address) } };
        input_patches.push_back(std::move(patch));
        *reinterpret_cast<uint16_t *>(address) = masks[index];
    }
    return true;
}

template <typename Function>
bool patch_slot(uintptr_t address, void *replacement, Function *original);

void __fastcall key_update(void *self, void *) {
    if (branch_anchor >= 0) ++branch_hook_calls;
    const auto keys = reinterpret_cast<uintptr_t>(self);
    const auto table = value<uint32_t>(keys);
    const auto found = key_updates.find(table);
    if (found == key_updates.end()) return;
    int previous[10] = {};
    if (readable(keys + 0x38, sizeof(previous)))
        std::memcpy(previous, reinterpret_cast<const void *>(keys + 0x38), sizeof(previous));
    found->second(self);
    if (branch_anchor < 0 || !channel || channel->status != kLoading ||
        channel->command != kBranch) return;
    const LONG index = channel->ordinal - branch_anchor;
    if (index < 0 || index >= static_cast<LONG>(branch_inputs.size())) return;
    const auto battle = value<uint32_t>(0x008985E4);
    const auto player = value<uint32_t>(battle + 0xc + branch_side * 4);
    const auto owner = value<uint32_t>(player + 0x750);
    if (value<uint32_t>(owner) != keys || !readable(keys + 0x38, 0x2c)) return;
    ++branch_applied;
    const unsigned mask = branch_inputs[index];
    const auto axis = [](int old, bool negative, bool positive) {
        if (negative) return std::min(old, 0) - 1;
        if (positive) return std::max(old, 0) + 1;
        return 0;
    };
    *reinterpret_cast<int *>(keys + 0x38) = axis(previous[0], mask & 1, mask & 2);
    *reinterpret_cast<int *>(keys + 0x3c) = axis(previous[1], mask & 4, mask & 8);
    for (unsigned bit = 0; bit < 8; ++bit)
        *reinterpret_cast<unsigned *>(keys + 0x40 + bit * 4) =
            mask & (1U << (bit + 4)) ? static_cast<unsigned>(previous[bit + 2]) + 1 : 0;
    *reinterpret_cast<uint16_t *>(keys + 0x60) = static_cast<uint16_t>(mask);
    *reinterpret_cast<uint16_t *>(keys + 0x62) = static_cast<uint16_t>(mask);
}

bool install_key_updates() {
    const auto battle = value<uint32_t>(0x008985E4);
    if (!battle) return false;
    bool installed = false;
    for (unsigned side = 0; side < 2; ++side) {
        const auto player = value<uint32_t>(battle + 0xc + side * 4);
        const auto owner = value<uint32_t>(player + 0x750);
        const auto keys = value<uint32_t>(owner);
        const auto table = value<uint32_t>(keys);
        if (!table) continue;
        if (key_updates.count(table)) {
            if (value<uint32_t>(table + 4) != reinterpret_cast<uintptr_t>(key_update)) {
                KeyUpdate original = nullptr;
                if (patch_slot(table + 4, reinterpret_cast<void *>(key_update), &original) &&
                    original && original != reinterpret_cast<KeyUpdate>(key_update))
                    key_updates[table] = original;
            }
            installed |= value<uint32_t>(table + 4) ==
                reinterpret_cast<uintptr_t>(key_update);
            continue;
        }
        KeyUpdate original = nullptr;
        if (patch_slot(table + 4, reinterpret_cast<void *>(key_update), &original) &&
            original && original != reinterpret_cast<KeyUpdate>(key_update)) {
            key_updates[table] = original;
            installed = true;
        }
    }
    return installed;
}

int load_replay(const char *path, bool from_seek) {
    restore_input_patches(); replay_buffers.clear();
    channel_buffers[0].clear(); channel_buffers[1].clear();
    snapshots.clear();
    automatic_slots.clear();
    if (!path || !path[0]) { acknowledge(-2); return 0; }
    auto read_replay = reinterpret_cast<bool(__thiscall *)(void *, const char *)>(0x0042EAC0);
    const bool loaded = read_replay(reinterpret_cast<void *>(0x00898718), path);
    if (!loaded) { replay_active = false; acknowledge(-3); return 0; }
    *reinterpret_cast<volatile uint8_t *>(0x0089A248 + 0x74) = 0xff;
    auto set_mode = reinterpret_cast<void(__cdecl *)(int, int)>(0x0043E9A0);
    set_mode(3, 2);
    replay_active = true;
    speed_percent = 100;
    speed_step_phase = 0;
    previous_frame = 0xffffffffu;
    channel->ordinal = -1;
    channel->engine_frame = -1;
    open_trace();
    channel->status = from_seek ? kLoading : kPlaying;
    if (!from_seek) acknowledge(kPlaying);
    return 6;
}

int title_step(void *self, Scene original) {
    retry_visible_cursor();
    if (!channel) return original(self);
    if (channel->sequence != channel->acknowledged && channel->command == kRelease) {
        restart_seek = false;
        snapshots.clear(); automatic_slots.clear();
        close_trace(); replay_active = false; restore_pacing(); acknowledge(kIdle);
        return original(self);
    }
    if (restart_seek) {
        restart_seek = false;
        const int next = load_replay(active_replay, true);
        return next ? next : original(self);
    }
    if (channel->sequence != channel->acknowledged) {
        if (channel->command == kLoad) {
            strncpy_s(active_replay, channel->replay_path, _TRUNCATE);
            strncpy_s(active_trace, channel->trace_path, _TRUNCATE);
            simulation_trace = channel->target >= 1;
            trace_start_ordinal = channel->target >= 2 ? channel->target - 2 : 0;
            channel->status = kLoading;
            const int next = load_replay(active_replay, false);
            if (next) return next;
        } else acknowledge(-4);
    }
    return original(self);
}

int __fastcall logo_hook(void *self, void *) { return title_step(self, original_logo); }
int __fastcall title_hook(void *self, void *) { return title_step(self, original_title); }

int battle_step(void *self) {
    retry_visible_cursor();
    if (!channel) return original_battle(self);
    if (channel->sequence != channel->acknowledged) {
        switch (channel->command) {
        case kFreeControl:
            // Hand the current game scene back without returning to title,
            // swapping player input, rewinding, or forcing a terminal scene.
            restore_input_patches(); replay_buffers.clear();
            channel_buffers[0].clear(); channel_buffers[1].clear();
            branch_inputs.clear(); branch_anchor = -1;
            snapshots.clear(); automatic_slots.clear();
            close_trace(); replay_active = false; restart_seek = false;
            seek_target = -1; speed_percent = 100; speed_step_phase = 0;
            restore_pacing(); acknowledge(kIdle);
            return original_battle(self);
        case kRelease:
            restore_input_patches(); replay_buffers.clear();
            channel_buffers[0].clear(); channel_buffers[1].clear();
            branch_inputs.clear(); branch_anchor = -1;
            snapshots.clear(); automatic_slots.clear();
            close_trace(); replay_active = false; restart_seek = false;
            restore_pacing(); speed_percent = 100; channel->ordinal = -1; acknowledge(kIdle);
            return 2; // Back to title. The game process remains alive.
        case kLoad:
            restore_input_patches(); replay_buffers.clear();
            channel_buffers[0].clear(); channel_buffers[1].clear();
            branch_inputs.clear(); branch_anchor = -1;
            snapshots.clear(); automatic_slots.clear();
            close_trace(); replay_active = false; restore_pacing();
            return 2; // Process load request from the title scene.
        case kPause:
            if (!replay_active) { acknowledge(-4); break; }
            restore_pacing(); if (trace) std::fflush(trace); acknowledge(kPaused);
            break;
        case kPlay:
            if (!replay_active) { acknowledge(-4); break; }
            restore_pacing(); acknowledge(kPlaying);
            break;
        case kSeek:
            if (!replay_active || channel->target < 0) { acknowledge(-4); break; }
            seek_target = channel->target;
            if (seek_target == channel->ordinal) {
                restore_pacing();
                if (trace) std::fflush(trace);
                acknowledge(kPaused);
                break;
            }
            channel->status = kLoading;
            if (seek_target < channel->ordinal) {
                bool restored = false;
                for (LONG slot = seek_target / 600 * 600; slot >= 600; slot -= 600) {
                    if (!snapshots.contains(slot)) continue;
                    LONG ordinal = channel->ordinal;
                    LONG frame = channel->engine_frame;
                    LONG phase = channel->phase;
                    if (!snapshots.restore(slot, ordinal, frame, phase, trace)) continue;
                    channel->ordinal = ordinal;
                    channel->engine_frame = frame;
                    channel->phase = phase;
                    previous_frame = static_cast<uint32_t>(frame);
                    restored = true;
                    break;
                }
                if (!restored) {
                    restart_seek = true;
                    close_trace(); replay_active = false;
                    return 2;
                }
                if (channel->ordinal == seek_target) {
                    restore_pacing(); acknowledge(kPaused);
                }
            }
            break;
        case kSpeed:
            if (!replay_active || channel->target != 50 && channel->target != 75 &&
                channel->target != 100 && channel->target != 150 &&
                channel->target != 200 && channel->target != 400) { acknowledge(-4); break; }
            speed_percent = channel->target;
            speed_step_phase = 0;
            acknowledge(channel->status == kPaused ? kPaused : kPlaying);
            break;
        case kCapture:
            if (!replay_active) { acknowledge(-4); break; }
            speed_percent = 100;
            speed_step_phase = 0;
            acknowledge(kLoading);
            break;
        case kSaveSnapshot:
            if (!replay_active || channel->status != kPaused ||
                channel->target != channel->ordinal || !snapshots.initialize()) {
                acknowledge(-6); break;
            }
            if (snapshots.count() >= native_checkpoint::MaxImages &&
                !snapshots.contains(channel->target) && !automatic_slots.empty()) {
                snapshots.erase(*automatic_slots.begin());
                automatic_slots.erase(automatic_slots.begin());
            }
            if (!snapshots.save(channel->target, channel->ordinal,
                    channel->engine_frame, channel->phase, trace)) acknowledge(-6);
            else {
                automatic_slots.erase(static_cast<LONG>(channel->target));
                acknowledge(kPaused);
            }
            break;
        case kRestoreSnapshot:
            if (!replay_active || channel->status != kPaused) { acknowledge(-7); break; }
            {
                LONG ordinal = channel->ordinal;
                LONG frame = channel->engine_frame;
                LONG phase = channel->phase;
                if (!snapshots.restore(channel->target, ordinal, frame, phase, trace)) {
                    acknowledge(-7);
                    break;
                }
                channel->ordinal = ordinal;
                channel->engine_frame = frame;
                channel->phase = phase;
                previous_frame = static_cast<uint32_t>(frame);
                seek_target = -1;
                restart_seek = false;
                restore_pacing();
                acknowledge(kPaused);
            }
            break;
        case kRestoreState:
            if (!replay_active || channel->status != kPaused) { acknowledge(-7); break; }
            {
                LONG ordinal = channel->ordinal, frame = channel->engine_frame;
                LONG phase = channel->phase;
                if (!snapshots.restore(channel->target, ordinal, frame, phase,
                        trace, false)) { acknowledge(-7); break; }
                if (channel->target == 0) {
                    snapshots.keep_only(0);
                    automatic_slots.clear();
                }
                close_trace();
                uint32_t restore_trace = 0;
                std::memcpy(&restore_trace, channel->replay_path, 4);
                simulation_trace = restore_trace != 1;
                trace_start_ordinal = restore_trace == 1 ? 0 : LONG_MAX;
                branch_inputs.clear(); branch_anchor = -1;
                channel->ordinal = ordinal; channel->engine_frame = frame;
                channel->phase = phase; previous_frame = static_cast<uint32_t>(frame);
                if (restore_trace == 1) {
                    strncpy_s(active_trace, channel->trace_path, _TRUNCATE);
                    open_trace();
                    if (!trace) { acknowledge(-9); break; }
                    previous_frame = 0xffffffffu;
                    --channel->ordinal;
                    capture();
                    if (channel->ordinal != ordinal) { acknowledge(-9); break; }
                }
                seek_target = -1; restore_pacing(); acknowledge(kPaused);
            }
            break;
        case kBranch:
            if (!replay_active || channel->status != kPaused) {
                acknowledge(-8); break;
            }
            {
                uint32_t side = 0, count = 0;
                std::memcpy(&side, channel->replay_path, 4);
                std::memcpy(&count, channel->replay_path + 4, 4);
                uint32_t input_index = 0;
                std::memcpy(&input_index, channel->replay_path + 8, 4);
                if (side > 1 || count == 0 || count > 250 ||
                    !channel->trace_path[0] ||
                    !snapshots.contains(channel->target)) { acknowledge(-8); break; }
                LONG ordinal = channel->ordinal, frame = channel->engine_frame;
                LONG phase = channel->phase;
                if (!snapshots.restore(channel->target, ordinal, frame, phase,
                        trace, false)) { acknowledge(-8); break; }
                branch_inputs.resize(count);
                std::memcpy(branch_inputs.data(), channel->replay_path + 12, count * 2);
                if (!patch_replay_inputs(input_index, side, branch_inputs)) {
                    branch_inputs.clear(); acknowledge(-10); break;
                }
                branch_side = side; branch_anchor = ordinal;
                branch_hook_calls = 0; branch_applied = 0;
                channel->ordinal = ordinal; channel->engine_frame = frame;
                channel->phase = phase; previous_frame = static_cast<uint32_t>(frame);
                strncpy_s(active_trace, channel->trace_path, _TRUNCATE);
                simulation_trace = true; trace_start_ordinal = ordinal;
                open_trace();
                if (!trace) { branch_inputs.clear(); branch_anchor = -1;
                    acknowledge(-9); break; }
                // Include the actual restored anchor; managed code must validate
                // it rather than silently taking this state from the baseline.
                previous_frame = 0xffffffffu;
                --channel->ordinal;
                capture();
                if (channel->ordinal != ordinal) { acknowledge(-9); break; }
                seek_target = ordinal + static_cast<LONG>(count);
                channel->status = kLoading;
            }
            break;
        default: acknowledge(-4); break;
        }
    }
    sync_replay_music();
    if (!replay_active) return original_battle(self);
    if (channel->status == kPaused || channel->status == -5) return 5;
    const bool seeking = channel->status == kLoading &&
        (channel->command == kSeek || channel->command == kBranch);
    const bool capturing = channel->status == kLoading && channel->command == kCapture;
    *reinterpret_cast<volatile int *>(0x008A0FF8) = seeking || capturing ? 0 :
        speed_percent < 100 ? 16 * 100 / speed_percent : 16;
    int steps = 1;
    // Replay seeking computes several engine frames per display tick. Each
    // frame is still captured; the paired render hook suppresses drawing.
    // The previous 64-frame cap made every branch rewind pay many extra UI ticks.
    if (seeking || capturing) steps = 256;
    else if (speed_percent >= 100) {
        speed_step_phase += speed_percent;
        steps = speed_step_phase / 100;
        speed_step_phase %= 100;
    }
    int result = 5;
    for (int i = 0; i < steps && result == 5; ++i) {
        result = original_battle(self);
        capture();
        if (seeking && channel->ordinal >= seek_target) {
            if (channel->command == kBranch) {
                channel->target = static_cast<LONG>(replay_buffers.size() * 100 +
                    channel_buffers[branch_side].size());
                restore_input_patches();
                branch_inputs.clear(); branch_anchor = -1;
            }
            restore_pacing(); if (trace) std::fflush(trace); acknowledge(kPaused);
            break;
        }
    }
    if (result != 5) {
        restore_pacing();
        if (trace) std::fflush(trace);
        channel->status = kPaused;
        if (channel->sequence != channel->acknowledged)
            acknowledge(seeking && channel->ordinal < seek_target ? -5 : kPaused);
        return 5; // Hold the terminal scene for the controller.
    }
    return result;
}
int __fastcall battle_hook(void *self, void *) {
    const int result = battle_step(self);
    // Covers natural replay end and music initialized during the first frame.
    sync_replay_music();
    return result;
}

bool __fastcall battle_render_hook(void *self, void *) {
    if (channel && replay_active && channel->status == kLoading &&
        (channel->command == kSeek || channel->command == kCapture ||
         channel->command == kBranch)) return true;
    return original_battle_render(self);
}

template <typename Function>
bool patch_slot(uintptr_t address, void *replacement, Function *original) {
    if (!readable(address, sizeof(void *))) return false;
    auto current = *reinterpret_cast<Function *>(address);
    if (!current || !readable(reinterpret_cast<uintptr_t>(current), 1)) return false;
    DWORD previous = 0;
    if (!VirtualProtect(reinterpret_cast<void *>(address), sizeof(void *), PAGE_EXECUTE_READWRITE, &previous))
        return false;
    *original = current;
    *reinterpret_cast<void **>(address) = replacement;
    DWORD ignored = 0;
    VirtualProtect(reinterpret_cast<void *>(address), sizeof(void *), previous, &ignored);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void *>(address), sizeof(void *));
    return true;
}
template <typename Function>
void restore_slot(uintptr_t address, Function original) {
    if (!original) return;
    DWORD previous = 0;
    if (!VirtualProtect(reinterpret_cast<void *>(address), sizeof(void *), PAGE_EXECUTE_READWRITE, &previous)) return;
    *reinterpret_cast<Function *>(address) = original;
    DWORD ignored = 0;
    VirtualProtect(reinterpret_cast<void *>(address), sizeof(void *), previous, &ignored);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void *>(address), sizeof(void *));
}

using FindWindowWide = HWND(WINAPI *)(LPCWSTR, LPCWSTR);
FindWindowWide score_tracker_find_window = nullptr;
uintptr_t score_tracker_window_slot = 0;

HWND WINAPI find_score_tracker_game_window(LPCWSTR class_name, LPCWSTR window_name) {
    // FindWindow only searches top-level windows. ScoreTracker lazily creates
    // its ImGui context on the loading screen, after we may have embedded the
    // game as a child. Give this one module its own verified game HWND; all
    // other searches retain the original API semantics.
    if (!window_name && reinterpret_cast<uintptr_t>(class_name) > 0xffff &&
        lstrcmpW(class_name, L"th123_110a") == 0) {
        DWORD owner = 0;
        wchar_t actual_class[64] = {};
        if (cursor_target && GetWindowThreadProcessId(cursor_target, &owner) &&
            owner == GetCurrentProcessId() &&
            GetClassNameW(cursor_target, actual_class, 64) &&
            lstrcmpW(actual_class, class_name) == 0)
            return cursor_target;
    }
    return score_tracker_find_window(class_name, window_name);
}

bool install_score_tracker_window_lookup() {
    const auto module = GetModuleHandleW(L"ScoreTracker.dll");
    if (!module || score_tracker_window_slot) return true;
    const auto base = reinterpret_cast<uintptr_t>(module);
    if (!readable(base, sizeof(IMAGE_DOS_HEADER))) return false;
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x100000)
        return false;
    const auto nt_address = base + dos->e_lfanew;
    if (!readable(nt_address, sizeof(IMAGE_NT_HEADERS32))) return false;
    const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS32 *>(nt_address);
    if (nt->Signature != IMAGE_NT_SIGNATURE ||
        nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC) return false;
    const auto &imports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!imports.VirtualAddress || imports.Size < sizeof(IMAGE_IMPORT_DESCRIPTOR)) return false;
    for (size_t offset = 0; offset + sizeof(IMAGE_IMPORT_DESCRIPTOR) <= imports.Size;
            offset += sizeof(IMAGE_IMPORT_DESCRIPTOR)) {
        const auto address = base + imports.VirtualAddress + offset;
        if (!readable(address, sizeof(IMAGE_IMPORT_DESCRIPTOR))) return false;
        const auto descriptor = reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR *>(address);
        if (!descriptor->Name) break;
        if (!descriptor->OriginalFirstThunk || !descriptor->FirstThunk) continue;
        for (size_t index = 0; index < nt->OptionalHeader.SizeOfImage / sizeof(IMAGE_THUNK_DATA32); ++index) {
            const auto name_address = base + descriptor->OriginalFirstThunk + index * sizeof(IMAGE_THUNK_DATA32);
            if (!readable(name_address, sizeof(IMAGE_THUNK_DATA32))) return false;
            const auto name = reinterpret_cast<const IMAGE_THUNK_DATA32 *>(name_address)->u1.AddressOfData;
            if (!name) break;
            if (IMAGE_SNAP_BY_ORDINAL32(name)) continue;
            const auto text = base + name + offsetof(IMAGE_IMPORT_BY_NAME, Name);
            constexpr char expected[] = "FindWindowW";
            if (!readable(text, sizeof(expected)) ||
                std::memcmp(reinterpret_cast<const void *>(text), expected, sizeof(expected)) != 0) continue;
            const auto slot = base + descriptor->FirstThunk + index * sizeof(IMAGE_THUNK_DATA32);
            if (!patch_slot(slot, reinterpret_cast<void *>(find_score_tracker_game_window),
                    &score_tracker_find_window)) return false;
            score_tracker_window_slot = slot;
            return true;
        }
    }
    // A different implementation without this import needs no adaptation.
    return true;
}
}

extern "C" __declspec(dllexport) DWORD WINAPI InitializeReplayProbe(void *window_value) {
    auto window = reinterpret_cast<HWND>(window_value);
    DWORD owner = 0;
    if (!window || !IsWindow(window) || !GetWindowThreadProcessId(window, &owner) ||
        owner != GetCurrentProcessId()) return 0;
    cursor_target = window;
    if (initialized) return 1;
    if (reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)) != 0x00400000) return 0;
    wchar_t name[96] = {};
    swprintf_s(name, L"Local\\ReplayLabProbeV8_%lu", GetCurrentProcessId());
    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
        0, sizeof(ProbeChannel), name);
    if (!mapping) return 0;
    channel = reinterpret_cast<ProbeChannel *>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS,
        0, 0, sizeof(ProbeChannel)));
    if (!channel) { CloseHandle(mapping); mapping = nullptr; return 0; }
    std::memset(channel, 0, sizeof(*channel));
    channel->ordinal = -1; channel->engine_frame = -1;
    if (!install_score_tracker_window_lookup() ||
        !patch_slot(0x00857740 + 4, reinterpret_cast<void *>(logo_hook), &original_logo) ||
        !patch_slot(0x00857FAC + 4, reinterpret_cast<void *>(title_hook), &original_title) ||
        !patch_slot(0x008574A0 + 4, reinterpret_cast<void *>(battle_hook), &original_battle) ||
        !patch_slot(0x008574A0 + 8, reinterpret_cast<void *>(battle_render_hook),
            &original_battle_render)) {
        restore_slot(0x00857740 + 4, original_logo);
        restore_slot(0x00857FAC + 4, original_title);
        restore_slot(0x008574A0 + 4, original_battle);
        restore_slot(0x008574A0 + 8, original_battle_render);
        restore_slot(score_tracker_window_slot, score_tracker_find_window);
        score_tracker_window_slot = 0;
        UnmapViewOfFile(channel); channel = nullptr;
        CloseHandle(mapping); mapping = nullptr;
        return 0;
    }
    channel->magic = kMagic; channel->version = kVersion;
    initialized = true;
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    if (reason == DLL_PROCESS_DETACH) close_trace();
    return TRUE;
}
