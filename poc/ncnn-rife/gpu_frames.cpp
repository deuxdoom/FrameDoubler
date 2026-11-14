// Original GPU RGB conversion, dense-time fill and input reuse via public ncnn C++ APIs.
#include "gpu_frames.h"
#include <net.h>
#include <pipeline.h>
#include <command.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <stdexcept>
#include <vector>

static const char* unpack_shader=R"glsl(#version 450
layout(local_size_x=8,local_size_y=8,local_size_z=1) in;
layout(binding=0) readonly buffer Raw {uint raw[];};
layout(binding=1) writeonly buffer Image {float image[];};
layout(binding=2) writeonly buffer Time {float times[];};
layout(push_constant) uniform parameter {int w;int h;int pw;int ph;int stride;int deep;float step;} p;
void main(){int x=int(gl_GlobalInvocationID.x),y=int(gl_GlobalInvocationID.y),c=int(gl_GlobalInvocationID.z);
 if(x>=p.pw||y>=p.ph||c>=3)return;
 int component=(min(y,p.h-1)*p.w+min(x,p.w-1))*3+c;
 int bits=p.deep!=0?16:8;int perword=32/bits;
 uint value=(raw[component/perword]>>((component%perword)*bits))&uint(p.deep!=0?65535:255);
 precise float normalized=float(value)*(p.deep!=0?1.0/65535.0:1.0/255.0);
 image[c*p.stride+y*p.pw+x]=normalized;
 if(c==0)times[y*p.pw+x]=p.step;
}
)glsl";
static const char* pack_shader=R"glsl(#version 450
layout(local_size_x=64,local_size_y=1,local_size_z=1) in;
layout(binding=0) readonly buffer Image {float image[];};
layout(binding=1) writeonly buffer Raw {uint raw[];};
layout(binding=2) writeonly buffer Flags {uint flags[];};
layout(push_constant) uniform parameter {int w;int h;int pw;int stride;int pack;int deep;int words;} p;
shared uint invalid;
void main(){uint i=gl_GlobalInvocationID.x;if(gl_LocalInvocationID.x==0)invalid=0;barrier();
 if(i<uint(p.words)){uint packed=0;int bits=p.deep!=0?16:8;int perword=32/bits;
  for(int lane=0;lane<perword;lane++){int component=int(i)*perword+lane;if(component>=p.w*p.h*3)break;
   int pixel=component/3,c=component%3,y=pixel/p.w,x=pixel%p.w;
   float value=image[(c/p.pack)*p.stride*p.pack+(y*p.pw+x)*p.pack+c%p.pack];
   if(isnan(value)||isinf(value)){atomicOr(invalid,1);value=0.0;}
   precise float scaled=clamp(value,0.0,1.0)*(p.deep!=0?65535.0:255.0);
   precise float rounded=scaled+0.5;packed|=uint(rounded)<<(lane*bits);
  }raw[i]=packed;
 }barrier();if(gl_LocalInvocationID.x==0)flags[gl_WorkGroupID.x]=invalid;
}
)glsl";

struct FDGpuFrames{
    ncnn::Net* net;const ncnn::VulkanDevice* device;ncnn::Option option;
    ncnn::Pipeline unpack,pack;ncnn::VkMat previous,time;
    std::vector<unsigned char> previous_bytes;int w,h,pw,ph;bool deep;
    FDGpuFrames(ncnn_net_t wrapper,int width,int height,int padded_w,int padded_h,bool is_deep):
      net(static_cast<ncnn::Net*>(wrapper->pthis)),device(net->vulkan_device()),option(net->opt),unpack(device),pack(device),w(width),h(height),pw(padded_w),ph(padded_h),deep(is_deep){
        option.blob_vkallocator=device->acquire_blob_allocator();option.workspace_vkallocator=option.blob_vkallocator;option.staging_vkallocator=device->acquire_staging_allocator();
        option.use_fp16_storage=false;option.use_fp16_packed=false;option.use_fp16_arithmetic=false;
        std::vector<uint32_t> spirv;
        if(ncnn::compile_spirv_module(unpack_shader,option,spirv))throw std::runtime_error("GPU RGB input shader compilation failed");
        unpack.set_local_size_xyz(8,8,1);if(unpack.create(spirv.data(),spirv.size()*4,{}))throw std::runtime_error("GPU RGB input pipeline failed");
        spirv.clear();if(ncnn::compile_spirv_module(pack_shader,option,spirv))throw std::runtime_error("GPU RGB output shader compilation failed");
        pack.set_local_size_xyz(64,1,1);if(pack.create(spirv.data(),spirv.size()*4,{}))throw std::runtime_error("GPU RGB output pipeline failed");
        time.create(pw,ph,1,4u,1,option.blob_vkallocator);
        if(time.empty())throw std::runtime_error("GPU timestep allocation failed");
    }
    ~FDGpuFrames(){previous.release();time.release();device->reclaim_blob_allocator(option.blob_vkallocator);device->reclaim_staging_allocator(option.staging_vkallocator);}
    void run(const unsigned char* a,const unsigned char* b,float step,unsigned char* output,double* timings){
        using Clock=std::chrono::steady_clock;auto start=Clock::now();
        size_t bytes=size_t(w)*h*3*(deep?2:1);ncnn::VkCompute command(device);ncnn::VkMat va,vb,raw_a,raw_b;
        std::vector<unsigned char> aligned_a((bytes+3)/4*4),aligned_b(aligned_a.size());
        auto upload=[&](const unsigned char* source,std::vector<unsigned char>& aligned,ncnn::VkMat& raw,ncnn::VkMat& image){
            std::memcpy(aligned.data(),source,bytes);ncnn::Mat cpu(int(aligned.size()/4),aligned.data(),4u,1);
            command.record_clone(cpu,raw,option);image.create(pw,ph,3,4u,1,option.blob_vkallocator);
            if(image.empty())throw std::runtime_error("GPU RGB frame allocation failed");
            std::vector<ncnn::vk_constant_type> constants(7);constants[0].i=w;constants[1].i=h;constants[2].i=pw;constants[3].i=ph;
            constants[4].i=int(image.cstep);constants[5].i=deep;constants[6].f=step;
            command.record_pipeline(&unpack,{raw,image,time},constants,image);
        };
        bool reuse=previous_bytes.size()==bytes&&std::memcmp(previous_bytes.data(),a,bytes)==0;
        if(reuse)va=previous;else upload(a,aligned_a,raw_a,va);
        upload(b,aligned_b,raw_b,vb);
        auto extractor=net->create_extractor();extractor.set_blob_vkallocator(option.blob_vkallocator);extractor.set_workspace_vkallocator(option.workspace_vkallocator);extractor.set_staging_vkallocator(option.staging_vkallocator);
        if(extractor.input("in0",va)||extractor.input("in1",vb)||extractor.input("in2",time))throw std::runtime_error("GPU RIFE input failed");
        ncnn::VkMat result;if(extractor.extract("out0",result,command))throw std::runtime_error("GPU RIFE extraction failed");
        if(result.w!=pw||result.h!=ph||result.c*result.elempack!=3||result.elemsize!=size_t(4*result.elempack))throw std::runtime_error("Unexpected GPU output shape");
        int words=int((bytes+3)/4);ncnn::VkMat packed(words,4u,1,option.blob_vkallocator),flags((words+63)/64,4u,1,option.blob_vkallocator);
        if(packed.empty()||flags.empty())throw std::runtime_error("GPU output allocation failed");
        std::vector<ncnn::vk_constant_type> constants(7);constants[0].i=w;constants[1].i=h;constants[2].i=pw;constants[3].i=int(result.cstep);constants[4].i=result.elempack;constants[5].i=deep;constants[6].i=words;
        command.record_pipeline(&pack,{result,packed,flags},constants,packed);
        ncnn::Mat cpu,errors;command.record_clone(packed,cpu,option);command.record_clone(flags,errors,option);
        timings[0]=std::chrono::duration<double>(Clock::now()-start).count();start=Clock::now();
        if(command.submit_and_wait())throw std::runtime_error("GPU command submission failed");
        timings[1]=std::chrono::duration<double>(Clock::now()-start).count();start=Clock::now();
        const auto* invalid=reinterpret_cast<const uint32_t*>(errors.data);for(int i=0;i<flags.w;i++)if(invalid[i])throw std::runtime_error("Non-finite GPU result");
        std::memcpy(output,cpu.data,bytes);previous=vb;previous_bytes.assign(b,b+bytes);
        timings[2]=std::chrono::duration<double>(Clock::now()-start).count();
    }
};
FDGpuFrames* fd_gpu_frames_create(ncnn_net_t net,int w,int h,int pw,int ph,bool deep){return new FDGpuFrames(net,w,h,pw,ph,deep);}
void fd_gpu_frames_destroy(FDGpuFrames* frames){delete frames;}
void fd_gpu_frames_run(FDGpuFrames* frames,const unsigned char* a,const unsigned char* b,float step,unsigned char* output,double* timings){frames->run(a,b,step,output,timings);}
