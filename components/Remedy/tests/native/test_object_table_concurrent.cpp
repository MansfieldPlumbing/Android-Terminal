#include "object_table.h"
#include <iostream>
#include <thread>
#include <future>
#include <atomic>
#include <cassert>
#include <chrono>

struct sync_resource {
    int value{42};
    std::atomic<uint32_t>* destroy_count{nullptr};
};

static void sync_deleter(void* ptr) noexcept {
    if (!ptr) return;
    auto* res = static_cast<sync_resource*>(ptr);
    if (res->destroy_count) {
        res->destroy_count->fetch_add(1);
    }
    delete res;
}

struct blocking_deleter_context {
    std::promise<void> entered_promise;
    std::shared_future<void> release_future;
    std::atomic<uint32_t> destroy_count{0};
};

struct blocking_resource {
    blocking_deleter_context* ctx{nullptr};
};

static void blocking_deleter(void* ptr) noexcept {
    if (!ptr) return;
    auto* res = static_cast<blocking_resource*>(ptr);
    if (res->ctx) {
        res->ctx->destroy_count.fetch_add(1);
        res->ctx->entered_promise.set_value();
        res->ctx->release_future.wait();
    }
    delete res;
}

int main() {
    std::cout << "[TEST] Starting Deterministic Object Table Synchronization Test..." << std::endl;

    remedy::object_table table;

    // Part 1: Lease Hold, Timeout, Acquisition Rejection, Probe Slot Protection, and Retirement Retry Test
    std::atomic<uint32_t> r1_destroyed{0};
    auto* r1 = new sync_resource{42, &r1_destroyed};
    remedy_handle_t h1 = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, r1, sync_deleter);
    assert(h1 != REMEDY_INVALID_HANDLE);
    uint32_t slot1 = remedy_handle_slot(h1);
    uint32_t gen1 = remedy_handle_generation(h1);

    // 1. Hold a valid lease
    remedy_err_t acq_err = REMEDY_OK;
    auto lease1 = table.acquire<sync_resource>(h1, REMEDY_OBJECT_WORKER, &acq_err);
    assert(acq_err == REMEDY_OK);
    assert(static_cast<bool>(lease1));

    // 2. Call remove(h1, 0) to establish REVOKING without sleep
    remedy_err_t timeout_err = table.remove(h1, 0);

    // 3. Assert REMEDY_ERR_TIMEOUT
    assert(timeout_err == REMEDY_ERR_TIMEOUT);

    // 4. Assert deleter has not run
    assert(r1_destroyed.load() == 0);

    // 5. Assert generation has not advanced (h1 is invalid to callers because state is REVOKING)
    assert(!table.is_valid(h1, REMEDY_OBJECT_WORKER));

    // 6. Assert new acquisition returns REMEDY_ERR_REVOKING
    {
        remedy_err_t err_rev = REMEDY_OK;
        auto lease_rev = table.acquire<sync_resource>(h1, REMEDY_OBJECT_WORKER, &err_rev);
        assert(err_rev == REMEDY_ERR_REVOKING);
        assert(!static_cast<bool>(lease_rev));
    }

    // PROBE INSERTION ASSERTION: Prove timeout does not publish or reuse the slot while lease remains held
    std::atomic<uint32_t> probe_destroyed{0};
    auto* probe_res = new sync_resource{999, &probe_destroyed};
    remedy_handle_t probe_handle = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, probe_res, sync_deleter);

    assert(probe_handle != REMEDY_INVALID_HANDLE);
    assert(remedy_handle_slot(probe_handle) != remedy_handle_slot(h1));
    assert(table.remove(probe_handle) == REMEDY_OK);
    assert(probe_destroyed.load() == 1);

    // 7. Release lease
    lease1.reset();

    // 8. Retry remove(h1, 2000) and assert REMEDY_OK
    remedy_err_t retry_err = table.remove(h1, 2000);
    assert(retry_err == REMEDY_OK);

    // 9. Assert deleter runs exactly once
    assert(r1_destroyed.load() == 1);

    // Part 2: Deterministic Finalizer Race Proof
    {
        blocking_deleter_context ctx;
        std::promise<void> release_promise;
        ctx.release_future = release_promise.get_future().share();

        // Store entered_future before launching remover thread
        std::future<void> entered_future = ctx.entered_promise.get_future();

        auto* b_res = new blocking_resource{&ctx};
        remedy_handle_t bh = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, b_res, blocking_deleter);
        assert(bh != REMEDY_INVALID_HANDLE);

        std::atomic<remedy_err_t> first_remove_result{REMEDY_ERR_TIMEOUT};

        // Thread 1 calls remove(bh), which claims finalizer ownership and enters blocking_deleter
        std::thread remover1([&]() {
            first_remove_result.store(table.remove(bh, 2000));
        });

        // Bounded readiness assertion
        assert(entered_future.wait_for(std::chrono::seconds(2)) == std::future_status::ready);

        // While Thread 1 is blocked inside deleter, Thread 2 calls remove(bh)
        // Must return EXACTLY REMEDY_ERR_REVOKING (active finalizer exclusion)
        remedy_err_t second_remove_result = table.remove(bh, 2000);
        assert(second_remove_result == REMEDY_ERR_REVOKING);

        // Unblock Thread 1 deleter
        release_promise.set_value();

        remover1.join();

        assert(first_remove_result.load() == REMEDY_OK);
        assert(ctx.destroy_count.load() == 1);
    }

    // Part 3: Executable Proof of Single Free-List Publication & Slot Reuse Generation
    {
        // Setup: Retire slot S at generation G
        std::atomic<uint32_t> initial_destroyed{0};
        auto* init_res = new sync_resource{1, &initial_destroyed};
        remedy_handle_t init_h = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, init_res, sync_deleter);

        uint32_t slot_S = remedy_handle_slot(init_h);
        uint32_t gen_G = remedy_handle_generation(init_h);

        assert(table.remove(init_h) == REMEDY_OK);
        assert(initial_destroyed.load() == 1);

        // 1. Insert Resource A -> assert A receives slot S at generation G + 1 (honoring wraparound rule)
        std::atomic<uint32_t> a_destroyed{0};
        auto* res_A = new sync_resource{10, &a_destroyed};
        remedy_handle_t h_A = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, res_A, sync_deleter);

        uint32_t slot_A = remedy_handle_slot(h_A);
        uint32_t gen_A = remedy_handle_generation(h_A);
        uint32_t expected_gen_A = (gen_G + 1 == 0) ? 1 : (gen_G + 1);

        assert(slot_A == slot_S);
        assert(gen_A == expected_gen_A);

        // 2. Leave A live!
        assert(table.is_valid(h_A, REMEDY_OBJECT_WORKER));

        // 3. Insert Resource B -> assert B receives a slot OTHER than S (proves S was published to free_list_ only once!)
        std::atomic<uint32_t> b_destroyed{0};
        auto* res_B = new sync_resource{20, &b_destroyed};
        remedy_handle_t h_B = table.insert(REMEDY_OBJECT_WORKER, REMEDY_INVALID_HANDLE, res_B, sync_deleter);

        uint32_t slot_B = remedy_handle_slot(h_B);
        assert(slot_B != slot_S);

        // Cleanup A and B
        assert(table.remove(h_A) == REMEDY_OK);
        assert(table.remove(h_B) == REMEDY_OK);
        assert(a_destroyed.load() == 1);
        assert(b_destroyed.load() == 1);
    }

    std::cout << "[TEST] Deterministic Object Table Synchronization Test PASSED!" << std::endl;
    return 0;
}
