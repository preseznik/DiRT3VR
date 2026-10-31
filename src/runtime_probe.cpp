#include <windows.h>
#include <d3d11.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
// JSON is kept dependency-free: OpenXR supplies UTF-8 strings and we escape the
// complete control range, including errors containing a path or quotation mark.
std::string Json(const std::string& value) {
    std::ostringstream out; out << '"';
    const char hex[] = "0123456789abcdef";
    for(const unsigned char ch : value) {
        if(ch == '"' || ch == '\\') out << '\\' << char(ch);
        else if(ch < 0x20) out << "\\u00" << hex[ch >> 4] << hex[ch & 15];
        else out << char(ch);
    }
    out << '"'; return out.str();
}
struct Instance {
    XrInstance value{};
    ~Instance() { if(value) xrDestroyInstance(value); }
};
struct XrFailure : std::runtime_error {
    XrResult result;
    XrFailure(const char* operation, XrResult code) : std::runtime_error(operation), result(code) {}
};
void Check(XrResult result, const char* operation) {
    if(XR_FAILED(result)) throw XrFailure(operation, result);
}
std::string Version(XrVersion value) {
    return std::to_string(XR_VERSION_MAJOR(value)) + "." + std::to_string(XR_VERSION_MINOR(value)) + "." + std::to_string(XR_VERSION_PATCH(value));
}
void Failure(const std::string& operation, XrResult code, const std::string& detail) {
    std::cout << "{\"schema\":1,\"success\":false,\"architecture\":\"x86\",\"stereo\":false,\"sessionCreated\":false,\"operation\":"
              << Json(operation) << ",\"xrResult\":" << code << ",\"error\":" << Json(detail) << "}\n";
}
}

int wmain(int argc, wchar_t** argv) {
    Instance instance;
    try {
        if(argc != 1) {
            if(argc != 3 || wcscmp(argv[1], L"--runtime-json") != 0)
                throw std::runtime_error("Usage: DiRT3VR.RuntimeProbe.exe [--runtime-json <absolute manifest path>]");
            const std::filesystem::path path(argv[2]);
            if(!path.is_absolute() || !std::filesystem::is_regular_file(path))
                throw std::runtime_error("The runtime manifest must be an existing absolute file path");
            if(!SetEnvironmentVariableW(L"XR_RUNTIME_JSON", path.c_str()))
                throw std::runtime_error("Unable to select the runtime manifest for this probe process");
        }

        uint32_t count{};
        Check(xrEnumerateInstanceExtensionProperties(nullptr, 0, &count, nullptr), "enumerateExtensions");
        std::vector<XrExtensionProperties> extensions(count, {XR_TYPE_EXTENSION_PROPERTIES});
        Check(xrEnumerateInstanceExtensionProperties(nullptr, count, &count, extensions.data()), "enumerateExtensions");
        bool d3d11{};
        for(const auto& extension : extensions)
            d3d11 |= strcmp(extension.extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME) == 0;
        if(!d3d11) throw XrFailure("D3D11 extension unavailable", XR_ERROR_EXTENSION_NOT_PRESENT);

        const char* enabled[] = {XR_KHR_D3D11_ENABLE_EXTENSION_NAME};
        XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
        strcpy_s(create.applicationInfo.applicationName, "DiRT3VR runtime probe");
        create.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
        create.enabledExtensionCount = 1; create.enabledExtensionNames = enabled;
        Check(xrCreateInstance(&create, &instance.value), "createInstance");
        XrInstanceProperties properties{XR_TYPE_INSTANCE_PROPERTIES};
        Check(xrGetInstanceProperties(instance.value, &properties), "getInstanceProperties");

        XrSystemGetInfo get{XR_TYPE_SYSTEM_GET_INFO}; get.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        XrSystemId system{};
        Check(xrGetSystem(instance.value, &get, &system), "getSystem");
        XrSystemProperties systemProperties{XR_TYPE_SYSTEM_PROPERTIES};
        Check(xrGetSystemProperties(instance.value, system, &systemProperties), "getSystemProperties");
        PFN_xrGetD3D11GraphicsRequirementsKHR graphics{};
        Check(xrGetInstanceProcAddr(instance.value, "xrGetD3D11GraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&graphics)), "getGraphicsRequirementsFunction");
        if(!graphics) throw std::runtime_error("Runtime returned an empty D3D11 requirements function");
        XrGraphicsRequirementsD3D11KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
        Check(graphics(instance.value, system, &requirements), "getGraphicsRequirements");

        count = 0;
        Check(xrEnumerateViewConfigurationViews(instance.value, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &count, nullptr), "enumerateStereoViews");
        if(count != 2) throw std::runtime_error("Runtime did not provide exactly two primary stereo views");
        std::vector<XrViewConfigurationView> views(count, {XR_TYPE_VIEW_CONFIGURATION_VIEW});
        Check(xrEnumerateViewConfigurationViews(instance.value, system, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, count, &count, views.data()), "enumerateStereoViews");
        if(count != 2) throw std::runtime_error("Runtime changed its primary stereo view count");

        std::cout << "{\"schema\":1,\"success\":true,\"architecture\":\"x86\",\"stereo\":false,\"sessionCreated\":false,\"runtimeName\":"
                  << Json(properties.runtimeName) << ",\"runtimeVersion\":" << Json(Version(properties.runtimeVersion))
                  << ",\"systemName\":" << Json(systemProperties.systemName)
                  << ",\"orientationTracking\":" << (systemProperties.trackingProperties.orientationTracking ? "true" : "false")
                  << ",\"positionTracking\":" << (systemProperties.trackingProperties.positionTracking ? "true" : "false")
                  << ",\"graphicsRequirements\":{\"adapterLuidLow\":" << requirements.adapterLuid.LowPart
                  << ",\"adapterLuidHigh\":" << requirements.adapterLuid.HighPart
                  << ",\"minFeatureLevel\":" << unsigned(requirements.minFeatureLevel) << "},\"views\":[";
        for(size_t i = 0; i < views.size(); ++i) {
            if(i) std::cout << ',';
            const auto& view = views[i];
            std::cout << "{\"width\":" << view.recommendedImageRectWidth << ",\"height\":" << view.recommendedImageRectHeight
                      << ",\"maxWidth\":" << view.maxImageRectWidth << ",\"maxHeight\":" << view.maxImageRectHeight
                      << ",\"samples\":" << view.recommendedSwapchainSampleCount << '}';
        }
        std::cout << "]}\n";
        return 0;
    } catch(const XrFailure& error) {
        char description[XR_MAX_RESULT_STRING_SIZE]{};
        if(instance.value) xrResultToString(instance.value, error.result, description);
        Failure(error.what(), error.result, description[0] ? description : "OpenXR runtime is unavailable or incompatible");
    } catch(const std::exception& error) {
        Failure("probe", XR_ERROR_RUNTIME_FAILURE, error.what());
    }
    return 1;
}
