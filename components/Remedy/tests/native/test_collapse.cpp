#include "domain.h"
#include "object_table.h"
#include "remedy/ports/worker_port.h"
#include <iostream>
#include <cassert>

int main() {
    std::cout << "[TEST] Starting Domain Collapse Conformance Test..." << std::endl;

    remedy::object_table table(16);

    // 1. Create Domain
    remedy_handle_t domain_h = table.insert(REMEDY_OBJECT_DOMAIN, REMEDY_INVALID_HANDLE, nullptr);
    remedy::domain d(domain_h, &table);

    assert(d.state() == REMEDY_DOMAIN_ACTIVE);

    // 2. Attach dummy worker handle to domain
    remedy_handle_t worker_h = table.insert(REMEDY_OBJECT_WORKER, domain_h, nullptr);
    assert(table.is_valid(worker_h, REMEDY_OBJECT_WORKER));

    remedy_err_t attach_res = d.attach_worker(nullptr, worker_h);
    assert(attach_res == REMEDY_OK);

    // 3. Perform Collapse
    remedy_err_t collapse_res = d.collapse(500);
    assert(collapse_res == REMEDY_OK);
    assert(d.state() == REMEDY_DOMAIN_DEAD);

    // 4. Verify worker handle was invalidated by domain collapse
    assert(!table.is_valid(worker_h, REMEDY_OBJECT_WORKER));

    // 5. Verify Collapse is idempotent
    remedy_err_t second_collapse = d.collapse(500);
    assert(second_collapse == REMEDY_OK);
    assert(d.state() == REMEDY_DOMAIN_DEAD);

    std::cout << "[TEST] Domain Collapse Conformance Test PASSED!" << std::endl;
    return 0;
}
