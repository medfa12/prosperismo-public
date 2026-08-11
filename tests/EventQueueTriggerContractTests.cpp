#include "kernel/eventQueue.h"
#include "libs/errno.h"

#include <cstdio>
#include <cstdlib>

namespace Kyty::Libs {
void PrintNameImpl(const char*, const char*, const char*) {}
} // namespace Kyty::Libs

namespace {

namespace EventQueue = Libs::LibKernel::EventQueue;
using Libs::LibKernel::KERNEL_ERROR_ETIMEDOUT;

void Check(bool value, const char* text) {
	if (!value) {
		std::fprintf(stderr, "EventQueueTriggerContractTests: failed: %s\n", text);
		std::abort();
	}
}

void TestOneResultPerUserEventIdentity() {
	EventQueue::KernelEqueue queue = EventQueue::KERNEL_EQUEUE_INVALID;
	Check(EventQueue::KernelCreateEqueue(&queue, "trigger-identity") == OK, "create event queue");

	constexpr int level_id   = 37;
	auto* const   level_data = reinterpret_cast<void*>(0x1234);
	Check(EventQueue::KernelAddUserEvent(queue, level_id) == OK, "add level user event");
	Check(EventQueue::KernelTriggerUserEvent(queue, level_id, level_data) == OK,
	      "trigger level user event");

	EventQueue::KernelEvent         events[4] {};
	int                             out     = -1;
	Libs::LibKernel::KernelUseconds timeout = 0;
	events[1].ident                         = 0xfeedface;
	Check(EventQueue::KernelWaitEqueue(queue, events, 4, &out, &timeout) == OK,
	      "receive level user event");
	Check(out == 1, "one level event occupied multiple result slots");
	Check(events[1].ident == 0xfeedface, "unused result slot was modified");
	Check(events[0].ident == level_id && events[0].filter == EventQueue::KERNEL_EVFILT_USER &&
	          events[0].udata == level_data,
	      "level user event metadata");

	out = -1;
	Check(EventQueue::KernelWaitEqueue(queue, events, 4, &out, &timeout) == OK,
	      "receive persistent level user event again");
	Check(out == 1, "persistent level event identity changed across waits");

	Check(EventQueue::KernelDeleteUserEvent(queue, level_id) == OK, "delete level user event");

	constexpr int edge_id   = 38;
	auto* const   edge_data = reinterpret_cast<void*>(0x5678);
	Check(EventQueue::KernelAddUserEventEdge(queue, edge_id) == OK, "add edge user event");
	Check(EventQueue::KernelTriggerUserEvent(queue, edge_id, edge_data) == OK,
	      "trigger edge user event");

	out = -1;
	Check(EventQueue::KernelWaitEqueue(queue, events, 4, &out, &timeout) == OK,
	      "receive edge user event");
	Check(out == 1 && events[0].ident == edge_id && events[0].udata == edge_data,
	      "edge user event identity");

	out = -1;
	Check(EventQueue::KernelWaitEqueue(queue, events, 4, &out, &timeout) == KERNEL_ERROR_ETIMEDOUT,
	      "edge user event remained triggered after delivery");
	Check(out == 0, "edge user event timeout count");

	Check(EventQueue::KernelDeleteEqueue(queue) == OK, "delete event queue");
}

} // namespace

int main() {
	TestOneResultPerUserEventIdentity();
	return 0;
}
