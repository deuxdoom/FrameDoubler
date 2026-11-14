// Independent RGB8 RIFE worker. Dynamically loads the optional ORT plugin runtime.
// The binary protocol is identical to rife-poc; no Python or CUDA SDK is required.
#include <windows.h>
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wmacro-redefined" // ORT's non-MSVC SAL definitions overlap MinGW's Windows headers.
#include <onnxruntime_c_api.h>
#pragma clang diagnostic pop
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fcntl.h>
#include <io.h>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

using Clock=std::chrono::steady_clock;
const OrtApi* api=nullptr;
void check(OrtStatus* status){if(status){std::string error=api->GetErrorMessage(status);api->ReleaseStatus(status);throw std::runtime_error(error);}}
double seconds(Clock::time_point start){return std::chrono::duration<double>(Clock::now()-start).count();}
void read_exact(void* data,size_t count){if(fread(data,1,count,stdin)!=count)throw std::runtime_error("Truncated request");}
struct Runtime{
    HMODULE dll=nullptr;OrtEnv* env=nullptr;OrtSession* session=nullptr;OrtSessionOptions* options=nullptr;OrtMemoryInfo* memory=nullptr;
    bool registered=false;std::vector<const OrtEpDevice*> devices;
    Runtime(const std::filesystem::path& root){
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);AddDllDirectory(root.c_str());
        SetEnvironmentVariableW(L"NVIDIA_TF32_OVERRIDE",L"0");
        dll=LoadLibraryExW((root/L"onnxruntime.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if(!dll)throw std::runtime_error("Cannot load optional ONNX Runtime");
        auto get=reinterpret_cast<const OrtApiBase*(ORT_API_CALL*)()>(GetProcAddress(dll,"OrtGetApiBase"));
        if(!get||!(api=get()->GetApi(ORT_API_VERSION)))throw std::runtime_error("Incompatible ONNX Runtime C API");
        check(api->CreateEnv(ORT_LOGGING_LEVEL_WARNING,"FrameDoubler",&env));
        check(api->RegisterExecutionProviderLibrary(env,"NvTensorRTRTXExecutionProvider",(root/L"onnxruntime_providers_nv_tensorrt_rtx.dll").c_str()));registered=true;
        const OrtEpDevice* const* all=nullptr;size_t count=0;check(api->GetEpDevices(env,&all,&count));
        for(size_t i=0;i<count;i++){
            std::cerr<<"EP_DEVICE "<<api->EpDevice_EpName(all[i])<<"\n";
            if(std::string(api->EpDevice_EpName(all[i]))=="NvTensorRTRTXExecutionProvider")devices.push_back(all[i]);
        }
        if(devices.empty())throw std::runtime_error("No compatible TensorRT RTX execution device");
    }
    ~Runtime(){
        if(session)api->ReleaseSession(session);if(options)api->ReleaseSessionOptions(options);if(memory)api->ReleaseMemoryInfo(memory);
        if(registered){auto status=api->UnregisterExecutionProviderLibrary(env,"NvTensorRTRTXExecutionProvider");if(status)api->ReleaseStatus(status);}
        if(env)api->ReleaseEnv(env);if(dll)FreeLibrary(dll);
    }
    void load(const std::filesystem::path& model,int index,int width,int height,const std::filesystem::path& cache){
        if(index<0||size_t(index)>=devices.size())throw std::runtime_error("Invalid RTX device index");
        check(api->CreateSessionOptions(&options));
        check(api->AddSessionConfigEntry(options,"session.disable_cpu_ep_fallback","1"));
        check(api->SetIntraOpNumThreads(options,1));
        std::filesystem::create_directories(cache);
        std::string shape="in0:1x"+std::to_string(height)+"x"+std::to_string(width)+"x3;in1:1x"+std::to_string(height)+"x"+std::to_string(width)+"x3;time:1x1x1x1";
        std::string cache_utf8=cache.u8string();
        const char* keys[]={"enable_cuda_graph","nv_max_workspace_size","nv_runtime_cache_path","nv_profile_min_shapes","nv_profile_opt_shapes","nv_profile_max_shapes"};
        const char* values[]={"1","2147483648",cache_utf8.c_str(),shape.c_str(),shape.c_str(),shape.c_str()};
        const OrtEpDevice* selected=devices[index];
        check(api->SessionOptionsAppendExecutionProvider_V2(options,env,&selected,1,keys,values,6));
        check(api->CreateSession(env,model.c_str(),options,&session));
        const OrtEpDevice* actual=nullptr;check(api->SessionGetEpDeviceForOutputs(session,&actual,1));
        if(!actual||std::string(api->EpDevice_EpName(actual))!="NvTensorRTRTXExecutionProvider")throw std::runtime_error("Output was not assigned to the requested GPU EP");
        check(api->CreateCpuMemoryInfo(OrtArenaAllocator,OrtMemTypeDefault,&memory));
        std::cerr<<"READY provider=NvTensorRTRTXExecutionProvider cpu_fallback=disabled fp32=1 device="<<index<<"\n";
    }
};
struct Tensor{
    OrtValue* value=nullptr;
    Tensor(OrtMemoryInfo* memory,void* data,size_t bytes,const int64_t* shape,size_t rank,ONNXTensorElementDataType type){check(api->CreateTensorWithDataAsOrtValue(memory,data,bytes,shape,rank,type,&value));}
    ~Tensor(){if(value)api->ReleaseValue(value);}
};
int wmain(int argc,wchar_t** args){try{
    bool probe=argc==3&&std::wstring(args[1])==L"--probe";
    if(!probe&&argc!=9)throw std::runtime_error("usage: rife-ort MODEL WIDTH HEIGHT fp32 gpu:INDEX SCALE RUNTIME CACHE | --probe RUNTIME");
    Runtime runtime(probe?args[2]:args[7]);
    if(probe){std::cout<<"[";for(size_t i=0;i<runtime.devices.size();i++){
        auto device=api->EpDevice_Device(runtime.devices[i]);if(i)std::cout<<",";
        std::cout<<"{\"Index\":"<<i<<",\"VendorId\":"<<api->HardwareDevice_VendorId(device)<<",\"DeviceId\":"<<api->HardwareDevice_DeviceId(device)<<"}";
    }std::cout<<"]\n";return 0;}
    int width=std::stoi(args[2]),height=std::stoi(args[3]);
    if(width<2||height<2||width>8192||height>8192||std::wstring(args[4])!=L"fp32")throw std::runtime_error("Invalid dimensions or precision");
    std::wstring device(args[5]);if(device.rfind(L"gpu:",0)!=0)throw std::runtime_error("RTX requires an explicit execution device");
    int index=std::stoi(device.substr(4));double scale=std::stod(args[6]);if(scale!=.5&&scale!=1)throw std::runtime_error("Invalid scale");
    int align=int(64/scale),pw=(width+align-1)/align*align,ph=(height+align-1)/align*align;
    runtime.load(args[1],index,pw,ph,args[8]);
    size_t size=size_t(width)*height*3,padded=size_t(pw)*ph*3;
    std::vector<unsigned char> first(size),second(size),a(padded),b(padded),output(padded),cropped(pw==width?0:size);
    int64_t shape[]={1,ph,pw,3},time_shape[]={1,1,1,1};float step=.5f;
    Tensor ta(runtime.memory,a.data(),padded,shape,4,ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8),tb(runtime.memory,b.data(),padded,shape,4,ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8),
        tt(runtime.memory,&step,sizeof(step),time_shape,4,ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT),to(runtime.memory,output.data(),padded,shape,4,ONNX_TENSOR_ELEMENT_DATA_TYPE_UINT8);
    const char* input_names[]={"in0","in1","time"};const char* output_names[]={"out0"};
    const OrtValue* inputs[]={ta.value,tb.value,tt.value};OrtValue* outputs[]={to.value};
    auto pad=[&](const std::vector<unsigned char>& src,std::vector<unsigned char>& dst){for(int y=0;y<ph;y++){
        const auto* row=src.data()+size_t(std::min(y,height-1))*width*3;auto* target=dst.data()+size_t(y)*pw*3;
        std::copy_n(row,size_t(width)*3,target);for(int x=width;x<pw;x++)std::copy_n(row+(width-1)*3,3,target+x*3);
    }};
    _setmode(_fileno(stdin),_O_BINARY);_setmode(_fileno(stdout),_O_BINARY);
    for(;;){int command=fgetc(stdin);if(command==EOF||command=='Q')break;if(command!='P')throw std::runtime_error("Invalid command");
        read_exact(&step,4);read_exact(first.data(),size);read_exact(second.data(),size);
        if(!(step>0&&step<1))throw std::runtime_error("Invalid timestep");
        auto start=Clock::now();pad(first,a);pad(second,b);double timings[]={seconds(start),0,0};
        start=Clock::now();check(api->Run(runtime.session,nullptr,input_names,inputs,3,output_names,1,outputs));timings[1]=seconds(start);
        start=Clock::now();
        const unsigned char* result=output.data();
        if(pw!=width){for(int y=0;y<height;y++)std::copy_n(output.data()+size_t(y)*pw*3,size_t(width)*3,cropped.data()+size_t(y)*width*3);result=cropped.data();}
        timings[2]=seconds(start);
        if(fwrite(timings,1,sizeof(timings),stdout)!=sizeof(timings))throw std::runtime_error("Output failed");
        if(fwrite(result,1,size,stdout)!=size)throw std::runtime_error("Output failed");
        if(fflush(stdout))throw std::runtime_error("Output flush failed");
    }
    return 0;
}catch(const std::exception& error){std::cerr<<"ERROR: "<<error.what()<<"\n";return 1;}}
