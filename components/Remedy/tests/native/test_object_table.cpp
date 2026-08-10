#include "object_table.h"
#include <iostream>
#include <cassert>
#include <string>
#include <atomic>

struct dummy_resource {
    int id{42};
    std::string name{"test_resource"};
    std::atomic<uint32_t>* destroy_count{nullptr};
};

static void dummy_deleter(void* ptr) noexcept {
    if (!ptr) return;
    auto* res = static_cast<dummy_resource*>(ptr);
    if (res->destroy_count) {
        res->destroy_count->fetch_add(1);
    }
    delete res;
}

int main() {
    std::cout << "[TEST] Starting Object Table Conformance Test..." << std::endl;

    // 4. Invalid Insertion Rejection Test (null resource or null deleter)
    {
        remedy::object_table table;
        dummy_resource* dummy = new dummy_resource();
        assert(table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, nullptr, dummy_deleter) == REMEDY_INVALID_HANDLE);
        assert(table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, dummy, nullptr) == REMEDY_INVALID_HANDLE);
        delete dummy;
    }

    remedy::object_table table;
    assert(table.live_count() == 0);

    std::atomic<uint32_t> r1_destroyed{0};
    auto* r1 = new dummy_resource{42, "res1", &r1_destroyed};

    remedy_handle_t h1 = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, r1, dummy_deleter);
    assert(h1 != REMEDY_INVALID_HANDLE);
    uint32_t slot1 = remedy_handle_slot(h1);
    uint32_t gen1 = remedy_handle_generation(h1);
    assert(slot1 == 1);
    assert(gen1 == 1);
    assert(table.is_valid(h1, REMEDY_OBJECT_WORKER));
    assert(!table.is_valid(h1, REMEDY_OBJECT_ARENA));

    // 1. Valid Typed Acquisition Test
    {
        remedy_err_t err = REMEDY_OK;
        auto lease = table.acquire<dummy_resource>(h1, REMEDY_OBJECT_WORKER, &err);
        assert(err == REMEDY_OK);
        assert(static_cast<bool>(lease));
        assert(lease->id == 42);
        assert(lease->name == "res1");
    }

    // 2. Wrong-Type Acquisition Rejection Test
    {
        remedy_err_t err = REMEDY_OK;
        auto lease = table.acquire<dummy_resource>(h1, REMEDY_OBJECT_ARENA, &err);
        assert(err == REMEDY_ERR_WRONG_TYPE);
        assert(!static_cast<bool>(lease));
    }

    // 3. Stale-Generation Acquisition Rejection Test
    {
        remedy_handle_t fake_handle = remedy_handle_make(gen1 + 99, slot1);
        remedy_err_t err = REMEDY_OK;
        auto lease = table.acquire<dummy_resource>(fake_handle, REMEDY_OBJECT_WORKER, &err);
        assert(err == REMEDY_ERR_HANDLE_STALE);
        assert(!static_cast<bool>(lease));
    }

    // 5. Successful Removal & Exactly-Once Destruction Test
    remedy_err_t rem_err = table.remove(h1);
    assert(rem_err == REMEDY_OK);
    assert(r1_destroyed.load() == 1);
    assert(!table.is_valid(h1, REMEDY_OBJECT_WORKER));

    // 9. Duplicate Removal Rejection Test (after retirement)
    remedy_err_t dup_rem_err = table.remove(h1);
    assert(dup_rem_err == REMEDY_ERR_HANDLE_STALE);

    // 6. Generation Advance on Removal & 7. Advanced Generation on Reuse
    std::atomic<uint32_t> r2_destroyed{0};
    auto* r2 = new dummy_resource{100, "res2", &r2_destroyed};
    remedy_handle_t h2 = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, r2, dummy_deleter);

    uint32_t slot2 = remedy_handle_slot(h2);
    uint32_t gen2 = remedy_handle_generation(h2);
    assert(slot2 == slot1);
    uint32_t expected_gen2 = (gen1 + 1 == 0) ? 1 : (gen1 + 1);
    assert(gen2 == expected_gen2);
    assert(table.is_valid(h2, REMEDY_OBJECT_WORKER));

    // 8. Stale Handle Post-Reuse Test
    assert(!table.is_valid(h1, REMEDY_OBJECT_WORKER));
    {
        remedy_err_t err = REMEDY_OK;
        auto stale_lease = table.acquire<dummy_resource>(h1, REMEDY_OBJECT_WORKER, &err);
        assert(err == REMEDY_ERR_HANDLE_STALE);
        assert(!static_cast<bool>(stale_lease));
    }

    // 10. Orderly Destructor Invocation Test (table destruction invokes remaining active deleters)
    {
        std::atomic<uint32_t> scoped_destroyed{0};
        {
            remedy::object_table scoped_table;
            auto* scoped_res = new dummy_resource{999, "scoped", &scoped_destroyed};
            remedy_handle_t scoped_h = scoped_table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, scoped_res, dummy_deleter);
            assert(scoped_h != REMEDY_INVALID_HANDLE);
            assert(scoped_destroyed.load() == 0);
        } // scoped_table destructor runs here
        assert(scoped_destroyed.load() == 1);
    }

    // Clean up r2 remaining in table before table destructor runs
    assert(table.remove(h2) == REMEDY_OK);
    assert(r2_destroyed.load() == 1);

    std::cout << "[TEST] Object Table Conformance Test PASSED!" << std::endl;
    return 0;
}
