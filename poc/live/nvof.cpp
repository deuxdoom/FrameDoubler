#include "nvof.h"
#include "gpu.h"
#include "edgeaegis.h"
#include <d3dcompiler.h>
#include <cstring>
#include <memory>
#include <algorithm>
#include <tuple>

// Optional build dependency: official SDK headers (each carries its MIT notice).
#if __has_include("../../tools/dev/nvof/include/nvOpticalFlowD3D11.h")
#include "../../tools/dev/nvof/include/nvOpticalFlowD3D11.h"
#define FDL_HAS_NVOF 1
#endif

namespace fdl {
#ifdef FDL_HAS_NVOF
namespace {
const char* kLuma = R"(
cbuffer Params : register(b0) { uint2 size; uint2 grid; float phase; uint step; float2 pad; };
Texture2D<float4> src : register(t0);
RWTexture2D<unorm float> dst : register(u0);
[numthreads(16,8,1)] void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    dst[id.xy] = dot(src.Load(int3(id.xy,0)).rgb, float3(.299,.587,.114));
})";
const char* kExpand = R"(
cbuffer Params : register(b0) { uint2 size; uint2 grid; float phase; uint step; float2 pad; };
Texture2D<int2> raw : register(t0);
RWTexture2D<float2> dst : register(u0);
float2 flow(float2 p) {
    float2 g = p / float(step) - .5;
    int2 q = int2(floor(g)); float2 f = frac(g); float2 v = 0;
    [unroll] for (int i=0;i<4;++i) {
        int2 c = clamp(q+int2(i&1,i>>1),int2(0,0),int2(grid)-1);
        v += float2(raw.Load(int3(c,0))) * (((i&1)?f.x:1-f.x)*((i>>1)?f.y:1-f.y));
    }
    return v / 32.0;
}
[numthreads(16,8,1)] void main(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    dst[id.xy] = flow(float2(id.xy)+.5);
})";
const char* kWarp = R"(
cbuffer Params : register(b0) { uint2 size; uint2 grid; float phase; uint step; float2 pad; };
Texture2D<float4> frameA : register(t0);
Texture2D<float4> frameB : register(t1);
Texture2D<int2> backward : register(t2);
Texture2D<int2> forward : register(t3);
SamplerState linearClamp : register(s0);
RWTexture2D<unorm float4> dst : register(u0);
float2 flow(float2 p, bool back) {
    float2 g=p/float(step)-.5; int2 q=int2(floor(g)); float2 f=frac(g); float2 v=0;
    [unroll] for(int i=0;i<4;++i) {
        int2 c=clamp(q+int2(i&1,i>>1),int2(0,0),int2(grid)-1);
        float2 t=back?float2(backward.Load(int3(c,0))):float2(forward.Load(int3(c,0)));
        v+=t*(((i&1)?f.x:1-f.x)*((i>>1)?f.y:1-f.y));
    }
    return v/32.0;
}
[numthreads(16,8,1)] void main(uint3 id : SV_DispatchThreadID) {
    if(any(id.xy>=size))return;
    float2 p=float2(id.xy)+.5, a=p, b=p;
    // Invert each source-coordinate field at the requested output time.
    [unroll] for(int i=0;i<3;++i) {
        a=p-phase*flow(a,false); b=p-(1-phase)*flow(b,true);
    }
    float2 va=flow(a,false), vb=flow(b,true);
    float ca=length(va+flow(a+va,true)), cb=length(vb+flow(b+vb,false));
    float wa=(1-phase)/(1+ca*ca), wb=phase/(1+cb*cb);
    if(any(a<.5)||any(a>float2(size)-.5))wa=0;
    if(any(b<.5)||any(b>float2(size)-.5))wb=0;
    float3 ac=frameA.SampleLevel(linearClamp,a/float2(size),0).rgb;
    float3 bc=frameB.SampleLevel(linearClamp,b/float2(size),0).rgb;
    dst[id.xy]=float4(wa+wb>1e-5?(ac*wa+bc*wb)/(wa+wb):lerp(ac,bc,phase),1);
})";
}

struct NvofFlow::State {
    HMODULE module = nullptr;
    NV_OF_D3D11_API_FUNCTION_LIST api{};
    NvOFHandle handle = nullptr;
    int width=0, height=0, step=4, gw=0, gh=0;
    bool bidirectional=true;
    bool edgeAegis=false;
    int slotA=0,slotB=0;
    EdgeAegis refine;
    ComPtr<ID3D11Texture2D> input[2], vectors[2], staging;
    ComPtr<ID3D11UnorderedAccessView> lumaUav[2];
    ComPtr<ID3D11ShaderResourceView> flowSrv[2];
    NvOFGPUBufferHandle inputs[2]{}, outputs[2]{};
    ComPtr<ID3D11ComputeShader> luma, expand, warp;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> sampler;
    ~State() {
        if(api.nvOFUnregisterResourceD3D11) {
            for(auto h:inputs)if(h)api.nvOFUnregisterResourceD3D11(h);
            for(auto h:outputs)if(h)api.nvOFUnregisterResourceD3D11(h);
        }
        if(handle && api.nvOFDestroy)api.nvOFDestroy(handle);
        if(module)FreeLibrary(module);
    }
    bool status(NV_OF_STATUS result, const char* where, std::string& error) {
        if(result==NV_OF_SUCCESS)return true;
        error=std::string("NVOF ")+where+" status="+std::to_string(result);
        char detail[1024]{}; uint32_t bytes=sizeof detail;
        if(handle && api.nvOFGetLastError && api.nvOFGetLastError(handle,detail,&bytes)==NV_OF_SUCCESS && detail[0])
            error += ": " + std::string(detail, strnlen(detail,sizeof detail));
        return false;
    }
    bool caps(NV_OF_CAPS which, std::vector<uint32_t>& values, std::string& error) {
        uint32_t count=0;
        if(!status(api.nvOFGetCaps(handle,which,nullptr,&count),"caps count",error)||count==0||count>1024)return false;
        values.resize(count);
        return status(api.nvOFGetCaps(handle,which,values.data(),&count),"caps",error);
    }
    void bind(Gpu& gpu, float phase) {
        struct Params { uint32_t w,h,gw,gh; float phase; uint32_t step; float pad[2]; } p{
            uint32_t(width),uint32_t(height),uint32_t(gw),uint32_t(gh),phase,uint32_t(step),{0,0}};
        gpu.context()->UpdateSubresource(constants.Get(),0,nullptr,&p,0,0);
        ID3D11Buffer* buffer=constants.Get(); gpu.context()->CSSetConstantBuffers(0,1,&buffer);
    }
    void dispatch(Gpu& gpu, ID3D11ComputeShader* shader) {
        auto c=gpu.context(); c->CSSetShader(shader,nullptr,0); c->Dispatch((width+15)/16,(height+7)/8,1);
        ID3D11ShaderResourceView* srv[4]{}; ID3D11UnorderedAccessView* uav=nullptr;
        c->CSSetShaderResources(0,4,srv); c->CSSetUnorderedAccessViews(0,1,&uav,nullptr);
    }
};

bool NvofFlow::create(Gpu& gpu, int preset, std::string& error, bool bidirectional, bool edgeAegis) {
    destroy(); error.clear(); auto s=std::make_unique<State>();
    bidirectional=bidirectional||edgeAegis;s->edgeAegis=edgeAegis;
    s->module=LoadLibraryExW(L"nvofapi64.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!s->module){error="NVOF driver library unavailable";return false;}
    using Create=NV_OF_STATUS(NVOFAPI*)(uint32_t,NV_OF_D3D11_API_FUNCTION_LIST*);
    auto create=reinterpret_cast<Create>(GetProcAddress(s->module,"NvOFAPICreateInstanceD3D11"));
    if(!create){error="NVOF D3D11 entry unavailable";return false;}
    if(!s->status(create(NV_OF_API_VERSION,&s->api),"API",error))return false;
    if(!s->api.nvCreateOpticalFlowD3D11||!s->api.nvOFInit||!s->api.nvOFGetCaps||!s->api.nvOFExecute||!s->api.nvOFRegisterResourceD3D11||!s->api.nvOFUnregisterResourceD3D11||!s->api.nvOFDestroy||!s->api.nvOFGetSurfaceFormatCountD3D11||!s->api.nvOFGetSurfaceFormatD3D11){error="NVOF incomplete API";return false;}
    if(!s->status(s->api.nvCreateOpticalFlowD3D11(gpu.device(),gpu.context(),&s->handle),"device",error))return false;
    s->width=gpu.width(); s->height=gpu.height();s->bidirectional=bidirectional;
    std::vector<uint32_t> values;
    for(auto [cap,dimension,minimum] : {std::tuple{NV_OF_CAPS_WIDTH_MIN,s->width,true},std::tuple{NV_OF_CAPS_HEIGHT_MIN,s->height,true},std::tuple{NV_OF_CAPS_WIDTH_MAX,s->width,false},std::tuple{NV_OF_CAPS_HEIGHT_MAX,s->height,false}}) {
        if(!s->caps(cap,values,error))return false;
        if((minimum && dimension<int(values[0]))||(!minimum && dimension>int(values[0]))){error="NVOF input size unsupported";return false;}
    }
    if(!s->caps(NV_OF_CAPS_SUPPORTED_OUTPUT_GRID_SIZES,values,error))return false;
    if(std::find(values.begin(),values.end(),4)==values.end()){error="NVOF 4x4 vector grid unsupported";return false;}
    auto formatSupported=[&](NV_OF_BUFFER_USAGE usage,DXGI_FORMAT format) {
        uint32_t count=0;
        if(!s->status(s->api.nvOFGetSurfaceFormatCountD3D11(s->handle,usage,NV_OF_MODE_OPTICALFLOW,&count),"format count",error)||count==0||count>1024)return false;
        std::vector<DXGI_FORMAT> formats(count);
        return s->status(s->api.nvOFGetSurfaceFormatD3D11(s->handle,usage,NV_OF_MODE_OPTICALFLOW,formats.data()),"formats",error) && std::find(formats.begin(),formats.end(),format)!=formats.end();
    };
    if(!formatSupported(NV_OF_BUFFER_USAGE_INPUT,DXGI_FORMAT_R8_UNORM)||!formatSupported(NV_OF_BUFFER_USAGE_OUTPUT,DXGI_FORMAT_R16G16_SINT)){if(error.empty())error="NVOF required formats unsupported";return false;}
    NV_OF_INIT_PARAMS init{}; init.width=s->width; init.height=s->height;
    init.outGridSize=NV_OF_OUTPUT_VECTOR_GRID_SIZE_4; init.mode=NV_OF_MODE_OPTICALFLOW;
    init.perfLevel=preset==0?NV_OF_PERF_LEVEL_FAST:(preset==2?NV_OF_PERF_LEVEL_SLOW:NV_OF_PERF_LEVEL_MEDIUM);
    init.predDirection=bidirectional?NV_OF_PRED_DIRECTION_BOTH:NV_OF_PRED_DIRECTION_FORWARD; init.inputBufferFormat=NV_OF_BUFFER_FORMAT_GRAYSCALE8;
    if(!s->status(s->api.nvOFInit(s->handle,&init),"init",error))return false;
    s->gw=(s->width+3)/4; s->gh=(s->height+3)/4;
    auto image=[&](int w,int h,DXGI_FORMAT format,UINT flags,ComPtr<ID3D11Texture2D>& out) {
        D3D11_TEXTURE2D_DESC d{}; d.Width=w;d.Height=h;d.MipLevels=1;d.ArraySize=1;d.Format=format;d.SampleDesc.Count=1;d.BindFlags=flags;
        HRESULT hr=gpu.device()->CreateTexture2D(&d,nullptr,&out);
        if(FAILED(hr)){error="NVOF texture "+hresult(hr);return false;}return true;
    };
    for(int i=0;i<2;++i) {
        if(!image(s->width,s->height,DXGI_FORMAT_R8_UNORM,D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS,s->input[i]))return false;
        if(FAILED(gpu.device()->CreateUnorderedAccessView(s->input[i].Get(),nullptr,&s->lumaUav[i]))){error="NVOF luma view";return false;}
        if(!s->status(s->api.nvOFRegisterResourceD3D11(s->handle,s->input[i].Get(),&s->inputs[i]),"input registration",error))return false;
        if(i==0 || bidirectional) {
            if(!image(s->gw,s->gh,DXGI_FORMAT_R16G16_SINT,D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS,s->vectors[i]))return false;
            if(FAILED(gpu.device()->CreateShaderResourceView(s->vectors[i].Get(),nullptr,&s->flowSrv[i]))){error="NVOF flow view";return false;}
            if(!s->status(s->api.nvOFRegisterResourceD3D11(s->handle,s->vectors[i].Get(),&s->outputs[i]),"output registration",error))return false;
        }
    }
    for(auto [source,out]:{std::pair{kLuma,std::addressof(s->luma)},std::pair{kExpand,std::addressof(s->expand)},std::pair{kWarp,std::addressof(s->warp)}}) {
        ComPtr<ID3DBlob> blob,messages; HRESULT hr=D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&blob,&messages);
        if(FAILED(hr)){error=messages?std::string(static_cast<const char*>(messages->GetBufferPointer())):"NVOF shader compilation";return false;}
        if(FAILED(gpu.device()->CreateComputeShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,out->GetAddressOf()))){error="NVOF compute shader";return false;}
    }
    D3D11_BUFFER_DESC bd{32,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};
    if(FAILED(gpu.device()->CreateBuffer(&bd,nullptr,&s->constants))){error="NVOF constants";return false;}
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
    if(FAILED(gpu.device()->CreateSamplerState(&sd,&s->sampler))){error="NVOF sampler";return false;}
    if(edgeAegis&&!s->refine.create(gpu,error))return false;
    state_=s.release(); return true;
}
bool NvofFlow::estimate(Gpu& gpu,int slotA,int slotB,bool reset,std::string& error) {
    if(!state_){error="NVOF not ready";return false;}auto& s=*state_;
    s.slotA=slotA;s.slotB=slotB;
    s.bind(gpu,0);
    for(int i=0;i<2;++i){auto srv=gpu.frameView(i==0?slotA:slotB);auto uav=s.lumaUav[i].Get();gpu.context()->CSSetShaderResources(0,1,&srv);gpu.context()->CSSetUnorderedAccessViews(0,1,&uav,nullptr);s.dispatch(gpu,s.luma.Get());}
    NV_OF_EXECUTE_INPUT_PARAMS in{};in.inputFrame=s.inputs[1];in.referenceFrame=s.inputs[0];in.disableTemporalHints=reset?NV_OF_TRUE:NV_OF_FALSE;
    NV_OF_EXECUTE_OUTPUT_PARAMS out{};out.outputBuffer=s.outputs[0];out.bwdOutputBuffer=s.outputs[1];
    return s.status(s.api.nvOFExecute(s.handle,&in,&out),"execute",error);
}
void NvofFlow::motionVectors(Gpu& gpu,ID3D11UnorderedAccessView* target) {
    if(state_&&state_->edgeAegis){auto& s=*state_;s.refine.apply(gpu,gpu.frameView(s.slotA),gpu.frameView(s.slotB),s.flowSrv[0].Get(),s.flowSrv[1].Get(),s.step,target);return;}
    if(!state_)return;auto& s=*state_;s.bind(gpu,0);auto srv=s.flowSrv[0].Get();gpu.context()->CSSetShaderResources(0,1,&srv);gpu.context()->CSSetUnorderedAccessViews(0,1,&target,nullptr);s.dispatch(gpu,s.expand.Get());
}
void NvofFlow::interpolate(Gpu& gpu,int a,int b,float phase) {
    if(!state_||!state_->bidirectional)return;auto& s=*state_;s.bind(gpu,phase);
    ID3D11ShaderResourceView* srv[4]={gpu.frameView(a),gpu.frameView(b),s.flowSrv[0].Get(),s.flowSrv[1].Get()};auto uav=gpu.outputUav();auto sampler=s.sampler.Get();
    gpu.context()->CSSetShaderResources(0,4,srv);gpu.context()->CSSetUnorderedAccessViews(0,1,&uav,nullptr);gpu.context()->CSSetSamplers(0,1,&sampler);s.dispatch(gpu,s.warp.Get());
}
bool NvofFlow::readBackward(Gpu& gpu,std::vector<float>& xy,int& width,int& height) {
    if(!state_)return false;auto& s=*state_;width=s.gw;height=s.gh;
    if(!s.staging){D3D11_TEXTURE2D_DESC d{};s.vectors[0]->GetDesc(&d);d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;if(FAILED(gpu.device()->CreateTexture2D(&d,nullptr,&s.staging)))return false;}
    gpu.context()->CopyResource(s.staging.Get(),s.vectors[0].Get());D3D11_MAPPED_SUBRESOURCE m{};
    if(FAILED(gpu.context()->Map(s.staging.Get(),0,D3D11_MAP_READ,0,&m)))return false;
    xy.resize(size_t(width)*height*2);
    for(int y=0;y<height;++y){auto row=reinterpret_cast<const int16_t*>(static_cast<const uint8_t*>(m.pData)+size_t(y)*m.RowPitch);for(int x=0;x<width*2;++x)xy[(size_t(y)*width*2)+x]=float(row[x])/32.f;}
    gpu.context()->Unmap(s.staging.Get(),0);return true;
}
int NvofFlow::grid() const{return state_?state_->step:0;}
void NvofFlow::destroy(){delete state_;state_=nullptr;}
#else
bool NvofFlow::create(Gpu&,int,std::string& error,bool,bool){error="NVOF SDK headers were not present at build time";return false;}
bool NvofFlow::estimate(Gpu&,int,int,bool,std::string& error){error="NVOF not built";return false;}
void NvofFlow::motionVectors(Gpu&,ID3D11UnorderedAccessView*){}
void NvofFlow::interpolate(Gpu&,int,int,float){}
bool NvofFlow::readBackward(Gpu&,std::vector<float>&,int&,int&){return false;}
int NvofFlow::grid() const{return 0;}
void NvofFlow::destroy(){}
#endif
NvofFlow::~NvofFlow(){destroy();}
} // namespace fdl
