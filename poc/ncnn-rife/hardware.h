#pragma once
#include <string>
std::string fd_hardware_json();
int fd_choose_device(const std::string& request, bool& use_half);
