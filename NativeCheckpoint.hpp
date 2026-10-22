#pragma once

#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <io.h>
#include <map>
#include <set>
#include <vector>

// Mutable 1.10a battle state graph. The traversal follows the verified game
// layouts, not arbitrary heap pointers: render resources must not be rewound.
// Derived from the old project's state-saving rules and giuroll-hagb's MIT
// rollback structure traversal; see third_party/licenses/GIUROLL_LICENSE.txt.
namespace native_checkpoint {

constexpr size_t MaxImageBytes = 32u * 1024u * 1024u;
constexpr size_t MaxImages = 16;

struct Region {
    uintptr_t address = 0;
    std::vector<unsigned char> data;
};
struct Image {
    LONG ordinal = -1;
    LONG engine_frame = -1;
    LONG phase = 0;
    long trace_offset = -1;
    size_t bytes = 0;
    std::vector<Region> regions;
};

class Store {
    std::map<LONG, Image> images_;
    std::set<uintptr_t> retained_;
    DWORD battle_thread_ = 0;
    BOOL (WINAPI *heap_free_)(HANDLE, DWORD, LPVOID) = nullptr;
    static Store *active_;

    static bool accessible(uintptr_t address, size_t length, bool write = false) {
        if (address < 0x10000 || !length || address + length < address) return false;
        MEMORY_BASIC_INFORMATION page = {};
        if (!VirtualQuery(reinterpret_cast<void *>(address), &page, sizeof(page)) ||
            page.State != MEM_COMMIT || page.Protect & PAGE_GUARD) return false;
        const DWORD flags = page.Protect & 0xff;
        if (flags == PAGE_NOACCESS) return false;
        if (write && flags != PAGE_READWRITE && flags != PAGE_WRITECOPY &&
            flags != PAGE_EXECUTE_READWRITE && flags != PAGE_EXECUTE_WRITECOPY) return false;
        const auto end = reinterpret_cast<uintptr_t>(page.BaseAddress) + page.RegionSize;
        return address + length <= end;
    }
    static uintptr_t read_word(uintptr_t address) {
        return accessible(address, 4) ? *reinterpret_cast<const uint32_t *>(address) : 0;
    }
    static BOOL WINAPI keep_heap_allocation(HANDLE heap, DWORD flags, LPVOID memory) {
        auto *self = active_;
        if (self && self->heap_free_ && memory &&
            GetCurrentThreadId() == self->battle_thread_ &&
            heap == *reinterpret_cast<HANDLE *>(0x0089B404) &&
            self->retained_.count(reinterpret_cast<uintptr_t>(memory))) return TRUE;
        return self && self->heap_free_ ? self->heap_free_(heap, flags, memory) : FALSE;
    }

public:
    Store() = default;
    Store(const Store &) = delete;
    Store &operator=(const Store &) = delete;

    bool initialize() {
        if (heap_free_) return true;
        auto **slot = reinterpret_cast<void **>(0x00857170);
        if (!accessible(reinterpret_cast<uintptr_t>(slot), 4)) return false;
        auto original = reinterpret_cast<BOOL (WINAPI *)(HANDLE,DWORD,LPVOID)>(*slot);
        if (!original || !accessible(reinterpret_cast<uintptr_t>(original), 1)) return false;
        DWORD protection = 0;
        if (!VirtualProtect(slot, 4, PAGE_READWRITE, &protection)) return false;
        heap_free_ = original;
        active_ = this;
        *slot = reinterpret_cast<void *>(keep_heap_allocation);
        DWORD ignored = 0;
        VirtualProtect(slot, 4, protection, &ignored);
        return true;
    }

    void clear() {
        images_.clear();
        retained_.clear();
    }
    void keep_only(LONG slot) {
        for (auto it = images_.begin(); it != images_.end(); )
            if (it->first == slot) ++it;
            else it = images_.erase(it);
    }
    size_t count() const { return images_.size(); }
    bool contains(LONG slot) const { return images_.count(slot) != 0; }
    void erase(LONG slot) { images_.erase(slot); }

    bool save(LONG slot, LONG ordinal, LONG frame, LONG phase, FILE *trace) {
        if (slot < 0 || ordinal < 0 || !heap_free_ ||
            images_.size() >= MaxImages && !contains(slot)) return false;
        battle_thread_ = GetCurrentThreadId();
        const HANDLE heap = *reinterpret_cast<HANDLE *>(0x0089B404);
        if (!heap) return false;
        std::map<uintptr_t, size_t> allocations;
        PROCESS_HEAP_ENTRY entry = {};
        if (!HeapLock(heap)) return false;
        while (HeapWalk(heap, &entry)) {
            if ((entry.wFlags & PROCESS_HEAP_ENTRY_BUSY) && entry.cbData > 0 &&
                entry.cbData <= 1024 * 1024)
                allocations[reinterpret_cast<uintptr_t>(entry.lpData)] = entry.cbData;
        }
        HeapUnlock(heap);

        Image image;
        image.ordinal = ordinal;
        image.engine_frame = frame;
        image.phase = phase;
        if (trace) {
            std::fflush(trace);
            image.trace_offset = std::ftell(trace);
            if (image.trace_offset < 0) return false;
        }
        std::map<uintptr_t, size_t> indexed;
        bool overflow = false;
        auto owner_of = [&](uintptr_t address) {
            auto it = allocations.upper_bound(address);
            if (it == allocations.begin()) return allocations.end();
            --it;
            return address - it->first < it->second ? it : allocations.end();
        };
        auto remaining = [&](uintptr_t address) -> size_t {
            auto it = owner_of(address);
            return it == allocations.end() ? 0 : it->second - (address - it->first);
        };
        auto add = [&](uintptr_t address, size_t size) {
            if (!address || !size || overflow || address + size < address) return;
            const bool global = address >= 0x00880000 && address + size <= 0x008A0000;
            const auto allocation = remaining(address);
            if (!global && !allocation) return;
            if (allocation) size = (std::min)(size, allocation);
            if (!accessible(address, size, true)) return;
            const auto known = indexed.find(address);
            const size_t prior = known == indexed.end() ? 0 : image.regions[known->second].data.size();
            if (size <= prior) return;
            if (image.bytes + size - prior > MaxImageBytes) { overflow = true; return; }
            if (known == indexed.end()) {
                indexed[address] = image.regions.size();
                image.regions.push_back({address, {}});
            }
            auto &region = image.regions[indexed[address]];
            const auto *start = reinterpret_cast<const unsigned char *>(address);
            region.data.assign(start, start + size);
            image.bytes += size - prior;
        };
        auto word = [&](uintptr_t address, unsigned offset = 0u) {
            return read_word(address + offset);
        };
        auto vector = [&](uintptr_t address) {
            const auto first = word(address), last = word(address, 8);
            if (last >= first && last - first <= 1024 * 1024) add(first, last - first);
        };
        auto list = [&](uintptr_t address, size_t object_bytes) {
            std::vector<uintptr_t> objects;
            add(address, 12);
            auto node = word(address);
            const auto count = word(address, 4);
            if (!node || count > 10000) return objects;
            add(node, 12);
            node = word(node);
            for (uintptr_t n = 0; n < count && node; ++n) {
                add(node, 12);
                const auto object = word(node, 8);
                if (object) {
                    objects.push_back(object);
                    if (object_bytes) add(object, object_bytes);
                }
                node = word(node);
            }
            return objects;
        };
        auto deque = [&](uintptr_t address, size_t block_bytes) {
            const auto table = word(address, 4), blocks = word(address, 8);
            if (blocks > 10000) return;
            add(table, blocks * 4);
            if (word(address, 16))
                for (uintptr_t n = 0; n < blocks; ++n) add(word(table, n * 4), block_bytes);
        };
        auto ring = [&](uintptr_t address, size_t block_bytes) {
            const auto table = word(address, 4), blocks = word(address, 8);
            const auto offset = word(address, 12), used = word(address, 16);
            if (!blocks || blocks > 10000 || used > blocks) return;
            add(table, blocks * 4);
            for (uintptr_t n = 0; n < used; ++n)
                add(word(table, ((n + offset) % blocks) * 4), block_bytes);
        };
        auto bullets = [&](uintptr_t address) {
            for (auto object : list(address, 0)) {
                const auto bytes = remaining(object);
                if (!bytes || bytes > 16384) continue;
                add(object, bytes);
                if (word(object, 0x3A4)) list(object + 0x3A4, 0);
                if (word(object, 0x17C)) list(object + 0x17C, 0);
                const auto extra = word(object, 0x35C);
                add(extra, remaining(extra));
                const auto info = word(object, 0x354);
                if (!info) continue;
                add(info, 0x54);
                for (unsigned field : {0x2Cu, 0x40u}) {
                    const auto table = word(info, field), count = word(info, field + 4);
                    if (count > 10000) continue;
                    add(table, count * 4);
                    for (uintptr_t n = 0; n < count; ++n) add(word(table, n * 4), 0x10);
                }
                const auto width = word(info, 8), height = word(info, 4);
                if (width && height && width <= 1024 * 1024 / 2 / height)
                    add(word(info, 0x50), width * height * 2 + 2);
            }
        };

        const uintptr_t roots[][2] = {
            {0x8985DC,0x24},{0x898600,0x6C},{0x8985D8,4},
            {0x8971B8,0x20},{0x8971D8,0x1400},{0x883CC8,4},
            {0x89A88C,4},{0x89A454,4},{0x896D64,8},{0x896B20,4},
            {0x89B65C,4},{0x89B660,0x9C0},{0x89C01C,4},
            {0x89AAF8,4},{0x88526C,4},{0x898718,0x128}
        };
        for (const auto &root : roots) add(root[0], root[1]);
        const auto input = word(0x8971C8);
        if (input) add(input + 4, 8);

        auto manager = word(0x8985EC);
        add(manager, 0xEC); vector(manager + 0x1C); vector(manager + 0x68);
        list(manager + 0x78, 0); list(manager + 0xA4, 0x180); deque(manager + 0x28, 0x10);
        manager = word(0x8985E0);
        add(manager, 0x118); list(manager + 4, 0); list(manager + 0x38, 0);
        add(manager + 0x38, 4);
        for (auto object : list(manager + 0x2C, 0))
            add(object, remaining(object) ? remaining(object) : 0x70);
        manager = word(0x8985F0);
        add(manager, 0x94); vector(manager + 0x10); vector(manager + 0x20);
        list(manager + 0x30, 0); list(manager + 0x5C, 0x178);
        manager = word(0x8985E8);
        add(manager, 0x688); vector(manager + 0x14); vector(manager + 0x24);
        list(manager + 0x34, 0); list(manager + 0x60, 0x178);
        ring(manager + 0x18C, 0xC); ring(manager + 0x1C0, 0xC);
        manager = word(0x8985E4);
        add(manager, 0x908);
        for (unsigned field : {0x30u,0x3Cu,0x48u,0x54u,0x60u,0x6Cu,0xBCu,0xE8u})
            list(manager + field, 0);
        vector(manager + 0x9C); vector(manager + 0xAC);
        const auto game = word(0x8985DC);
        add(game, 0x58); vector(game + 0x40);
        for (unsigned side = 0; side < 2; ++side) {
            const auto player = word(manager, 0xC + side * 4);
            const auto bytes = remaining(player);
            if (!player || !bytes || bytes > 16384) return false;
            add(player, bytes);
            bullets(player + 0x17C);
            if ((read_word(player + 0x34C) & 0xffu) == 5u) ring(player + 0x8BC, 0x2C);
            auto node = word(player, 0x718);
            const auto count = word(player, 0x71C);
            for (uintptr_t n = 0; n <= count && n < 10000 && node; ++n) {
                add(node, 0xF4);
                node = word(node);
            }
            const auto objects = word(player, 0x6F8);
            add(objects, 0x68);
            const auto first = word(objects + 0x10), last = word(objects + 0x18);
            if (last >= first && last - first < 40000)
                for (auto address = first; address < last; address += 4) {
                    const auto object = word(address);
                    if (object) { add(object, 8); add(object + 0x154, 4); }
                }
            vector(objects + 0x10); vector(objects + 0x20);
            list(objects + 0x30, 0); bullets(objects + 0x5C);
            deque(player + 0x7B0, 0x10); deque(player + 0x5E8, 0x98);
            deque(player + 0x5B0, 0x10); deque(player + 0x5FC, 0x10);
            const auto keys = word(player, 0x750);
            add(keys, remaining(keys));
            if (keys) { const auto keymap = word(keys); add(keymap, remaining(keymap)); }
        }
        const auto extra = word(0x89881C);
        if (extra) { add(extra, 0x50); deque(extra + 0x3C, 16); }
        if (overflow || image.regions.empty()) return false;
        for (const auto &region : image.regions) {
            const auto it = owner_of(region.address);
            if (it != allocations.end()) retained_.insert(it->first);
        }
        images_[slot] = std::move(image);
        return true;
    }

    bool restore(LONG slot, LONG &ordinal, LONG &frame, LONG &phase, FILE *trace,
                 bool restore_trace = true) {
        const auto found = images_.find(slot);
        if (found == images_.end()) return false;
        const auto &image = found->second;
        for (const auto &region : image.regions)
            if (!accessible(region.address, region.data.size(), true)) return false;
        if (restore_trace && image.trace_offset >= 0) {
            if (!trace || std::fflush(trace) != 0 || std::fseek(trace, 0, SEEK_END) != 0 ||
                std::ftell(trace) < image.trace_offset ||
                _chsize_s(_fileno(trace), image.trace_offset) != 0 ||
                std::fseek(trace, image.trace_offset, SEEK_SET) != 0) return false;
        }
        for (const auto &region : image.regions)
            std::memcpy(reinterpret_cast<void *>(region.address), region.data.data(), region.data.size());
        ordinal = image.ordinal;
        frame = image.engine_frame;
        phase = image.phase;
        return true;
    }
};

inline Store *Store::active_ = nullptr;
}
