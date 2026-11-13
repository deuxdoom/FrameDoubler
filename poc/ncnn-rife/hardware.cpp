// Capability discovery. Vendor/model strings are metadata, never execution gates.
#include "hardware.h"
#include <gpu.h>
#include <windows.h>
#include <algorithm>
#include <cstdlib>
#include <iostream>
#include <sstream>
#include <stdexcept>

namespace {
std::string quote(const std::string& value) {
    std::ostringstream out;
    out << '"';
    for (unsigned char ch : value) {
        if (ch == '"' || ch == '\\') out << '\\' << ch;
        else if (ch < 32) out << ' ';
        else out << ch;
    }
    out << '"';
    return out.str();
}
bool disabled() {
    const char* value = std::getenv("FRAMEDOUBLER_DISABLE_VULKAN");
    return value && std::string(value) == "1";
}
int count_devices() {
    if (disabled()) return 0;
    if (ncnn::create_gpu_instance() != 0) return 0;
    return ncnn::get_gpu_count();
}
uint64_t local_bytes(const ncnn::GpuInfo& info) {
    uint64_t result = 0;
    const auto& memory = info.physicalDeviceMemoryProperties();
    for (uint32_t i = 0; i < memory.memoryHeapCount; ++i)
        if (memory.memoryHeaps[i].flags & VK_MEMORY_HEAP_DEVICE_LOCAL_BIT) result += memory.memoryHeaps[i].size;
    return result;
}
}

std::string fd_hardware_json() {
    char model[512] = {};
    DWORD bytes = sizeof(model);
    LONG found = RegGetValueA(HKEY_LOCAL_MACHINE, "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",
                             "ProcessorNameString", RRF_RT_REG_SZ, nullptr, model, &bytes);
    const int count = count_devices();
    std::ostringstream out;
    out << "{\"cpu_model\":" << (found == ERROR_SUCCESS ? quote(model) : "null")
        << ",\"logical_processors\":" << GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)
        << ",\"vulkan_disabled\":" << (disabled() ? "true" : "false") << ",\"gpus\":[";
    for (int i = 0; i < count; ++i) {
        const auto& info = ncnn::get_gpu_info(i);
        const auto* device = ncnn::get_gpu_device(i);
        uint64_t budget = device ? uint64_t(device->get_heap_budget()) * 1024 * 1024 : 0;
        if (i) out << ',';
        out << "{\"index\":" << i << ",\"name\":" << quote(info.device_name())
            << ",\"vendor_id\":" << info.vendor_id() << ",\"device_id\":" << info.device_id()
            << ",\"type\":" << info.type() << ",\"api_version\":" << info.api_version()
            << ",\"driver_version\":" << info.driver_version() << ",\"driver_name\":" << quote(info.driver_name() ? info.driver_name() : "")
            << ",\"local_heap_bytes\":" << local_bytes(info)
            << ",\"dedicated_bytes\":" << (info.type() == 0 ? std::to_string(local_bytes(info)) : "null")
            << ",\"budget_bytes\":" << (budget ? std::to_string(budget) : "null")
            << ",\"budget_is_estimate\":" << (info.support_VK_EXT_memory_budget() ? "false" : "true")
            << ",\"fp16_storage\":" << (info.support_fp16_storage() ? "true" : "false")
            << ",\"fp16_arithmetic\":" << (info.support_fp16_arithmetic() ? "true" : "false")
            << ",\"cooperative_matrix\":" << (info.support_cooperative_matrix() ? "true" : "false")
            << ",\"max_workgroup_invocations\":" << info.max_workgroup_invocations()
            << ",\"rough_score\":" << info.rough_score() << '}';
    }
    out << "]}";
    return out.str();
}

int fd_choose_device(const std::string& request, bool& use_half) {
    if (request == "cpu") { use_half = false; return -1; }
    if (request != "gpu" && request != "auto" && request.rfind("gpu:", 0) != 0)
        throw std::runtime_error("device must be auto, gpu, gpu:INDEX or cpu");
    const int count = count_devices();
    if (count == 0) {
        if (request != "auto") throw std::runtime_error("No usable Vulkan device. Choose CPU mode for slower inference.");
        use_half = false;
        std::cerr << "FALLBACK cpu: Vulkan unavailable; CPU inference can be much slower.\n";
        return -1;
    }
    int index = -1;
    if (request.rfind("gpu:", 0) == 0) {
        size_t end = 0;
        index = std::stoi(request.substr(4), &end);
        if (end != request.size() - 4 || index < 0 || index >= count) throw std::runtime_error("Invalid Vulkan device index");
    } else {
        double best = -1;
        for (int i = 0; i < count; ++i) {
            const auto& info = ncnn::get_gpu_info(i);
            const auto* device = ncnn::get_gpu_device(i);
            if (!device || info.max_workgroup_invocations() < 64) continue;
            double score = info.rough_score() + std::min(16., double(device->get_heap_budget()) / 1024.);
            if (score > best) { best = score; index = i; }
        }
    }
    if (index < 0 || ncnn::get_gpu_info(index).max_workgroup_invocations() < 64)
        throw std::runtime_error("Vulkan device lacks the required compute workgroup size");
    const auto& info = ncnn::get_gpu_info(index);
    // The shader declares float16 types; require both advertised capabilities.
    // Arithmetic itself stays FP32. Unsupported devices take the FP32 path.
    use_half = use_half && info.support_fp16_storage() && info.support_fp16_arithmetic();
    std::cerr << "DEVICE index=" << index << " name=" << info.device_name()
              << " fp16_storage=" << use_half << " arithmetic=fp32\n";
    return index;
}
