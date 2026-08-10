#include "object_table.h"

namespace remedy {

object_table::object_table() {
    ensure_capacity(CHUNK_SIZE - 1);
}

object_table::~object_table() {
    std::unique_lock<std::mutex> table_lock(mutex_);

    // Pass 1: Validation pass under global lock hierarchy (mutex_ -> slot_mutex)
    for (uint32_t i = 1; i < high_watermark_; ++i) {
        object_slot* slot = get_slot(i);
        if (!slot) continue;

        std::lock_guard<std::mutex> slot_lock(slot->slot_mutex);

        // Verify pin_count == 0 and rundown_active == false
        if (slot->pin_count != 0 || slot->rundown_active) {
            std::abort();
        }

        // Verify resource_ptr and deleter consistency
        if ((slot->resource_ptr == nullptr) != (slot->deleter == nullptr)) {
            std::abort();
        }

        // LIVE or REVOKING slots must contain valid pairs
        if (slot->state == REMEDY_SLOT_LIVE || slot->state == REMEDY_SLOT_REVOKING) {
            if (slot->resource_ptr == nullptr || slot->deleter == nullptr) {
                std::abort();
            }
        }

        // FREE and RETIRED slots must contain neither
        if (slot->state == REMEDY_SLOT_FREE || slot->state == REMEDY_SLOT_RETIRED) {
            if (slot->resource_ptr != nullptr || slot->deleter != nullptr) {
                std::abort();
            }
        }
    }

    // Pass 2: Collection pass
    struct pending_destruction {
        void* res{nullptr};
        remedy_deleter_fn del{nullptr};
    };
    std::vector<pending_destruction> to_destroy;

    for (uint32_t i = 1; i < high_watermark_; ++i) {
        object_slot* slot = get_slot(i);
        if (!slot) continue;

        std::lock_guard<std::mutex> slot_lock(slot->slot_mutex);
        if (slot->resource_ptr != nullptr && slot->deleter != nullptr) {
            to_destroy.push_back({slot->resource_ptr, slot->deleter});
            slot->resource_ptr = nullptr;
            slot->deleter = nullptr;
            slot->state = REMEDY_SLOT_RETIRED;
        }
    }

    // Release table mutex before invoking deleters
    table_lock.unlock();

    // Invoke deleters outside all locks
    for (const auto& item : to_destroy) {
        if (item.del && item.res) {
            item.del(item.res);
        }
    }

    // Delete allocated chunk arrays only after deleter processing completes
    for (size_t c = 0; c < MAX_CHUNKS; ++c) {
        object_slot* chunk = chunks_[c].load(std::memory_order_relaxed);
        if (chunk) {
            delete[] chunk;
            chunks_[c].store(nullptr, std::memory_order_relaxed);
        }
    }
}

object_slot* object_table::get_slot(uint32_t slot_idx) const {
    if (slot_idx == 0) return nullptr;
    uint32_t chunk_idx = slot_idx / CHUNK_SIZE;
    uint32_t offset = slot_idx % CHUNK_SIZE;
    if (chunk_idx >= MAX_CHUNKS) return nullptr;

    object_slot* chunk = chunks_[chunk_idx].load(std::memory_order_acquire);
    if (!chunk) return nullptr;
    return &chunk[offset];
}

void object_table::ensure_capacity(uint32_t slot_idx) {
    uint32_t chunk_idx = slot_idx / CHUNK_SIZE;
    if (chunk_idx >= MAX_CHUNKS) return;

    if (!chunks_[chunk_idx].load(std::memory_order_relaxed)) {
        object_slot* new_chunk = new object_slot[CHUNK_SIZE]();
        chunks_[chunk_idx].store(new_chunk, std::memory_order_release);
    }
}

remedy_handle_t object_table::insert(remedy_object_type_t type, remedy_handle_t owner_domain, void* resource_ptr, remedy_deleter_fn deleter) {
    if (!resource_ptr || !deleter) {
        return REMEDY_INVALID_HANDLE;
    }

    std::lock_guard<std::mutex> lock(mutex_);

    uint32_t slot_idx = 0;
    bool popped_from_free = false;
    if (!free_list_.empty()) {
        slot_idx = free_list_.back();
        free_list_.pop_back();
        popped_from_free = true;
    } else {
        slot_idx = high_watermark_++;
        ensure_capacity(slot_idx);
    }

    object_slot* slot = get_slot(slot_idx);
    assert(slot != nullptr);

    std::lock_guard<std::mutex> slot_lock(slot->slot_mutex);

    if (popped_from_free) {
        if (slot->state != REMEDY_SLOT_FREE ||
            slot->rundown_active ||
            slot->pin_count != 0 ||
            slot->resource_ptr != nullptr ||
            slot->deleter != nullptr) {
            std::abort(); // Do not silently overwrite or repair a malformed free-list entry!
        }
    }

    if (slot->generation == 0) {
        slot->generation = 1;
    }

    slot->type = type;
    slot->owner_domain = owner_domain;
    slot->resource_ptr = resource_ptr;
    slot->deleter = deleter;
    slot->pin_count = 0;
    slot->rundown_active = false;
    slot->state = REMEDY_SLOT_LIVE;

    return remedy_handle_make(slot->generation, slot_idx);
}

remedy_err_t object_table::remove(remedy_handle_t handle, uint32_t timeout_ms) {
    uint32_t slot_idx = remedy_handle_slot(handle);
    uint32_t gen = remedy_handle_generation(handle);

    object_slot* slot = get_slot(slot_idx);
    if (!slot) return REMEDY_ERR_HANDLE_STALE;

    std::unique_lock<std::mutex> slot_lock(slot->slot_mutex);

    // 1. Generation mismatch, FREE, or RETIRED -> REMEDY_ERR_HANDLE_STALE
    if (slot->generation != gen || slot->state == REMEDY_SLOT_FREE || slot->state == REMEDY_SLOT_RETIRED) {
        return REMEDY_ERR_HANDLE_STALE;
    }

    // 2. LIVE state -> transition to REVOKING, claim rundown_active
    if (slot->state == REMEDY_SLOT_LIVE) {
        slot->state = REMEDY_SLOT_REVOKING;
        slot->rundown_active = true;
    } else if (slot->state == REMEDY_SLOT_REVOKING) {
        if (slot->rundown_active) {
            return REMEDY_ERR_REVOKING; // Another active finalizer running
        }
        slot->rundown_active = true; // Claim finalizer ownership for retry
    }

    // 3. Wait for pin_count == 0
    bool pins_drained = true;
    if (slot->pin_count > 0) {
        if (timeout_ms == 0) {
            pins_drained = (slot->pin_count == 0);
        } else {
            pins_drained = slot->slot_cv.wait_for(slot_lock, std::chrono::milliseconds(timeout_ms), [&] {
                return slot->pin_count == 0;
            });
        }
    }

    // 4. On timeout -> rollback rundown_active, leave REVOKING state and resource intact
    if (!pins_drained) {
        slot->rundown_active = false;
        return REMEDY_ERR_TIMEOUT;
    }

    // 5. Successful finalization (pins_drained == true)
    void* res = slot->resource_ptr;
    remedy_deleter_fn del = slot->deleter;

    slot->resource_ptr = nullptr;
    slot->deleter = nullptr;

    // Release slot_mutex before invoking deleter
    slot_lock.unlock();

    // Invoke deleter outside all locks
    if (del && res) {
        del(res);
    }

    // Reacquire locks in global lock order (mutex_ -> slot_mutex)
    std::lock_guard<std::mutex> table_lock(mutex_);
    std::lock_guard<std::mutex> slot_relock(slot->slot_mutex);

    slot->state = REMEDY_SLOT_RETIRED;

    uint32_t next_gen = slot->generation + 1;
    if (next_gen == 0) next_gen = 1;
    slot->generation = next_gen;

    slot->type = REMEDY_OBJECT_NONE;
    slot->owner_domain = REMEDY_INVALID_HANDLE;

    slot->state = REMEDY_SLOT_FREE;
    free_list_.push_back(slot_idx);

    slot->rundown_active = false;

    return REMEDY_OK;
}

bool object_table::is_valid(remedy_handle_t handle, remedy_object_type_t expected_type) const {
    uint32_t slot_idx = remedy_handle_slot(handle);
    uint32_t gen = remedy_handle_generation(handle);

    object_slot* slot = get_slot(slot_idx);
    if (!slot) return false;

    std::lock_guard<std::mutex> lock(slot->slot_mutex);

    if (slot->generation != gen) return false;
    if (slot->state != REMEDY_SLOT_LIVE) return false;
    if (expected_type != REMEDY_OBJECT_NONE && slot->type != expected_type) return false;

    return true;
}

uint32_t object_table::live_count() const {
    std::lock_guard<std::mutex> lock(mutex_);
    uint32_t count = 0;
    for (uint32_t i = 1; i < high_watermark_; ++i) {
        object_slot* slot = get_slot(i);
        if (slot) {
            std::lock_guard<std::mutex> slot_lock(slot->slot_mutex);
            if (slot->state == REMEDY_SLOT_LIVE) {
                count++;
            }
        }
    }
    return count;
}

} // namespace remedy
