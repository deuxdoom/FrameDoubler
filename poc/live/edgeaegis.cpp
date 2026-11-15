#include "edgeaegis.h"
#include "gpu.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>

namespace fdl {
namespace {
// Independently implemented from the motion-refinement design described in public reports.
// Choose an actual neighbouring motion rather than averaging across foreground/background.
const char* kSelect=R"(
cbuffer Params : register(b0) { uint2 size; uint2 grid; uint step; float3 unused; };
Texture2D<float4> previous : register(t0);
Texture2D<float4> current : register(t1);
Texture2D<int2> backward : register(t2);
Texture2D<int2> forward : register(t3);
SamplerState clampLinear : register(s0);
RWTexture2D<float4> selected : register(u0);
float2 raw(int2 q, bool reverse) {
    q=clamp(q,int2(0,0),int2(grid)-1);
    return float2(reverse?forward.Load(int3(q,0)):backward.Load(int3(q,0)))/32.0;
}
float2 field(float2 p,bool reverse) {
    float2 q=p/float(step)-.5;int2 b=int2(floor(q));float2 f=frac(q);
    return lerp(lerp(raw(b,reverse),raw(b+int2(1,0),reverse),f.x),
                lerp(raw(b+int2(0,1),reverse),raw(b+1,reverse),f.x),f.y);
}
float cost(float2 p,float2 v) {
    if(any(p+v<.5)||any(p+v>float2(size)-.5))return 1;
    float sum=0;
    [unroll] for(int k=0;k<5;k++) {
        int2 d=k==0?int2(0,0):k==1?int2(-1,0):k==2?int2(1,0):k==3?int2(0,-1):int2(0,1);
        float3 a=previous.SampleLevel(clampLinear,(p+v+d)/float2(size),0).rgb;
        float3 b=current.SampleLevel(clampLinear,(p+d)/float2(size),0).rgb;
        sum+=dot(abs(a-b),float3(.299,.587,.114));
    }
    return sum*.2;
}
[numthreads(16,8,1)] void main(uint3 id:SV_DispatchThreadID) {
    if(any(id.xy>=size))return;
    float2 p=float2(id.xy)+.5,base=field(p,false),best=base;
    float bestCost=cost(p,base),score=bestCost;
    int2 cell=int2(id.xy)/int(step);
    float variation=0;
    [unroll] for(int y=-1;y<=1;y++)[unroll]for(int x=-1;x<=1;x++)variation=max(variation,length(raw(cell+int2(x,y),false)-base));
    // Smooth, well-matched regions preserve the original dense field exactly.
    if(variation>.75||bestCost>.04) {
        [unroll] for(int k=0;k<10;k++) {
            float2 v=k==9?float2(0,0):raw(cell+int2(k%3-1,k/3-1),false);
            float c=cost(p,v),rank=c+.00015*min(length(v-base),32.0);
            if(rank<score){score=rank;bestCost=c;best=v;}
        }
    }
    float disagreement=length(best+field(p+best,true));
    if(any(p+best<.5)||any(p+best>float2(size)-.5))disagreement=1000;
    selected[id.xy]=float4(best,bestCost,disagreement);
})";
const char* kFill=R"(
cbuffer Params : register(b0) { uint2 size; uint2 grid; uint step; float3 unused; };
Texture2D<float4> selected : register(t0);
Texture2D<float4> current : register(t1);
RWTexture2D<float2> output : register(u0);
[numthreads(16,8,1)] void main(uint3 id:SV_DispatchThreadID) {
    if(any(id.xy>=size))return;
    int2 p=int2(id.xy);float4 center=selected.Load(int3(p,0));float2 result=center.xy;
    if(center.z>.08||center.w>3) {
        float3 color=current.Load(int3(p,0)).rgb;float best=1;
        [loop] for(int radius=4;radius<=128;radius*=2) {
            [unroll] for(int k=0;k<8;k++) {
                int2 direction=k==0?int2(-1,0):k==1?int2(1,0):k==2?int2(0,-1):k==3?int2(0,1):
                               k==4?int2(-1,-1):k==5?int2(1,-1):k==6?int2(-1,1):int2(1,1);
                int2 q=p+direction*radius;if(any(q<0)||any(q>=int2(size)))continue;
                float4 candidate=selected.Load(int3(q,0));if(candidate.z>.04||candidate.w>1)continue;
                float3 delta=abs(color-current.Load(int3(q,0)).rgb);
                float difference=max(delta.r,max(delta.g,delta.b));if(difference>.12)continue;
                float rank=difference*2+candidate.z*.5+float(radius)/5120.0;
                if(rank<best){best=rank;result=candidate.xy;}
            }
        }
    }
    output[id.xy]=result;
})";
}

bool EdgeAegis::create(Gpu& gpu,std::string& error) {
    for(auto [text,target]:{std::pair{kSelect,std::addressof(select_)},std::pair{kFill,std::addressof(fill_)}}) {
        ComPtr<ID3DBlob> code,messages;
        HRESULT hr=D3DCompile(text,strlen(text),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&code,&messages);
        if(FAILED(hr)){error=messages?std::string(static_cast<const char*>(messages->GetBufferPointer())):"Edge Aegis shader compilation";return false;}
        if(FAILED(gpu.device()->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,target->GetAddressOf()))){error="Edge Aegis compute shader";return false;}
    }
    D3D11_TEXTURE2D_DESC td{UINT(gpu.width()),UINT(gpu.height()),1,1,DXGI_FORMAT_R16G16B16A16_FLOAT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_UNORDERED_ACCESS,0,0};
    if(FAILED(gpu.device()->CreateTexture2D(&td,nullptr,&selected_))||FAILED(gpu.device()->CreateShaderResourceView(selected_.Get(),nullptr,&selectedView_))||FAILED(gpu.device()->CreateUnorderedAccessView(selected_.Get(),nullptr,&selectedWrite_))){error="Edge Aegis intermediate texture";return false;}
    D3D11_BUFFER_DESC bd{32,D3D11_USAGE_DEFAULT,D3D11_BIND_CONSTANT_BUFFER,0,0,0};
    if(FAILED(gpu.device()->CreateBuffer(&bd,nullptr,&constants_))){error="Edge Aegis constants";return false;}
    D3D11_SAMPLER_DESC sd{};sd.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;sd.AddressU=sd.AddressV=sd.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP;sd.MaxLOD=D3D11_FLOAT32_MAX;
    if(FAILED(gpu.device()->CreateSamplerState(&sd,&sampler_))){error="Edge Aegis sampler";return false;}
    return true;
}
void EdgeAegis::apply(Gpu& gpu,ID3D11ShaderResourceView* previous,ID3D11ShaderResourceView* current,
                      ID3D11ShaderResourceView* backward,ID3D11ShaderResourceView* forward,int step,ID3D11UnorderedAccessView* output) {
    struct {uint32_t w,h,gw,gh,step;float unused[3];} p{uint32_t(gpu.width()),uint32_t(gpu.height()),uint32_t((gpu.width()+step-1)/step),uint32_t((gpu.height()+step-1)/step),uint32_t(step),{}};
    auto c=gpu.context();c->UpdateSubresource(constants_.Get(),0,nullptr,&p,0,0);
    ID3D11Buffer* cb=constants_.Get();c->CSSetConstantBuffers(0,1,&cb);auto sampler=sampler_.Get();c->CSSetSamplers(0,1,&sampler);
    auto dispatch=[&](ID3D11ComputeShader* shader,ID3D11UnorderedAccessView* target){
        c->CSSetUnorderedAccessViews(0,1,&target,nullptr);c->CSSetShader(shader,nullptr,0);c->Dispatch((p.w+15)/16,(p.h+7)/8,1);
        ID3D11ShaderResourceView* empty[4]{};ID3D11UnorderedAccessView* none=nullptr;c->CSSetShaderResources(0,4,empty);c->CSSetUnorderedAccessViews(0,1,&none,nullptr);
    };
    ID3D11ShaderResourceView* inputs[]={previous,current,backward,forward};c->CSSetShaderResources(0,4,inputs);dispatch(select_.Get(),selectedWrite_.Get());
    ID3D11ShaderResourceView* fill[]={selectedView_.Get(),current};c->CSSetShaderResources(0,2,fill);dispatch(fill_.Get(),output);
}

bool EdgeAegis::selfTest(Gpu& gpu,std::string& detail) {
    const int w=gpu.width(),h=gpu.height(),gw=(w+3)/4,gh=(h+3)/4,shift=16;
    EdgeAegis refine;if(!refine.create(gpu,detail))return false;
    std::vector<uint8_t> pictures[2];std::vector<int16_t> vectors[2];
    const int left=w/3,right=w*2/3,top=h/4,bottom=h*3/4;
    for(int f=0;f<2;f++){
        pictures[f].resize(size_t(w)*h*4,255);vectors[f].resize(size_t(gw)*gh*2,0);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){
            const bool person=x>=left+f*shift&&x<right+f*shift&&y>=top&&y<bottom;
            const size_t i=(size_t(y)*w+x)*4;
            pictures[f][i]=person?235:uint8_t(25+(x/5+y/3)%25);pictures[f][i+1]=person?45:110;pictures[f][i+2]=person?30:160;
        }
        for(int y=0;y<gh;y++)for(int x=0;x<gw;x++){
            const int px=x*4+2,py=y*4+2;const bool person=px>=left+(f==0?shift:0)&&px<right+(f==0?shift:0)&&py>=top&&py<bottom;
            vectors[f][(size_t(y)*gw+x)*2]=person?int16_t((f==0?-shift:shift)*32):0;
        }
    }
    gpu.uploadRgba(0,pictures[0].data(),w*4,0);gpu.uploadRgba(1,pictures[1].data(),w*4,0);
    ComPtr<ID3D11Texture2D> raw[2],output,staging;ComPtr<ID3D11ShaderResourceView> srv[2];ComPtr<ID3D11UnorderedAccessView> uav;
    D3D11_TEXTURE2D_DESC d{UINT(gw),UINT(gh),1,1,DXGI_FORMAT_R16G16_SINT,{1,0},D3D11_USAGE_DEFAULT,D3D11_BIND_SHADER_RESOURCE,0,0};
    for(int f=0;f<2;f++){D3D11_SUBRESOURCE_DATA input{vectors[f].data(),UINT(gw*4),0};if(FAILED(gpu.device()->CreateTexture2D(&d,&input,&raw[f]))||FAILED(gpu.device()->CreateShaderResourceView(raw[f].Get(),nullptr,&srv[f])))return false;}
    d.Width=w;d.Height=h;d.Format=DXGI_FORMAT_R32G32_FLOAT;d.BindFlags=D3D11_BIND_UNORDERED_ACCESS;
    if(FAILED(gpu.device()->CreateTexture2D(&d,nullptr,&output))||FAILED(gpu.device()->CreateUnorderedAccessView(output.Get(),nullptr,&uav)))return false;
    d.BindFlags=0;d.Usage=D3D11_USAGE_STAGING;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    if(FAILED(gpu.device()->CreateTexture2D(&d,nullptr,&staging)))return false;
    auto read=[&](std::vector<float>& out){
        gpu.context()->CopyResource(staging.Get(),output.Get());D3D11_MAPPED_SUBRESOURCE m{};if(FAILED(gpu.context()->Map(staging.Get(),0,D3D11_MAP_READ,0,&m)))return false;
        out.resize(size_t(w)*h*2);for(int y=0;y<h;y++)memcpy(out.data()+size_t(y)*w*2,static_cast<uint8_t*>(m.pData)+size_t(y)*m.RowPitch,size_t(w)*8);gpu.context()->Unmap(staging.Get(),0);return true;
    };
    std::vector<float> result,again;refine.apply(gpu,gpu.frameView(0),gpu.frameView(1),srv[0].Get(),srv[1].Get(),4,uav.Get());if(!read(result))return false;
    refine.apply(gpu,gpu.frameView(0),gpu.frameView(1),srv[0].Get(),srv[1].Get(),4,uav.Get());if(!read(again)||result!=again)return false;
    double before=0,after=0;size_t count=0;
    for(int y=8;y<h-8;y++)for(int x=8;x<w-8;x++){
        const bool person=x>=left+shift&&x<right+shift&&y>=top&&y<bottom;const float truth=person?-shift:0;
        const double qx=(x+.5)/4-.5,qy=(y+.5)/4-.5;const int ix=int(floor(qx)),iy=int(floor(qy));double original=0;
        for(int k=0;k<4;k++)original+=vectors[0][(size_t(std::clamp(iy+(k>>1),0,gh-1))*gw+std::clamp(ix+(k&1),0,gw-1))*2]/32.0*((k&1)?qx-ix:1-(qx-ix))*((k>>1)?qy-iy:1-(qy-iy));
        before+=std::abs(original-truth);const float value=result[(size_t(y)*w+x)*2];if(!std::isfinite(value))return false;after+=std::abs(value-truth);++count;
    }
    // A flat zero-motion field must remain exactly zero, including the picture border.
    for(int f=0;f<2;f++){std::fill(vectors[f].begin(),vectors[f].end(),0);gpu.context()->UpdateSubresource(raw[f].Get(),0,nullptr,vectors[f].data(),gw*4,0);}
    refine.apply(gpu,gpu.frameView(0),gpu.frameView(0),srv[0].Get(),srv[1].Get(),4,uav.Get());if(!read(again)||std::any_of(again.begin(),again.end(),[](float v){return v!=0;}))return false;
    detail="boundary EPE "+std::to_string(before/count)+" -> "+std::to_string(after/count)+" px; deterministic; zero motion preserved";
    return after<before*.85;
}
}
