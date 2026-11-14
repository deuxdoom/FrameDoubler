#pragma once
#include <c_api.h>
#include <cstddef>
struct FDGpuFrames;
FDGpuFrames* fd_gpu_frames_create(ncnn_net_t net,int width,int height,int padded_w,int padded_h,bool deep);
void fd_gpu_frames_destroy(FDGpuFrames* frames);
void fd_gpu_frames_run(FDGpuFrames* frames,const unsigned char* first,const unsigned char* second,float step,unsigned char* output,double* timings);
