cmake_minimum_required(VERSION 3.20)
project(trackdub_maxine_lipsync_host LANGUAGES CXX)

# The NVIDIA Maxine AR SDK is not part of this repository. Point ARSDK_ROOT at a local install
# (the folder with nvar/include, nvar/src, bin and features).
set(ARSDK_ROOT "$ENV{TRACKDUB_MAXINE_ARSDK_ROOT}" CACHE PATH "NVIDIA Maxine AR SDK root")
if(NOT EXISTS "${ARSDK_ROOT}/nvar/include/nvAR.h")
  message(FATAL_ERROR "Set ARSDK_ROOT (or TRACKDUB_MAXINE_ARSDK_ROOT) to an AR SDK root containing nvar/include/nvAR.h.")
endif()
if(NOT EXISTS "${ARSDK_ROOT}/features/nvarlipsync/include/nvARLipSync.h")
  message(FATAL_ERROR "The LipSync feature is missing: expected ${ARSDK_ROOT}/features/nvarlipsync/include/nvARLipSync.h.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(trackdub-maxine-lipsync-host
  main.cpp
  "${ARSDK_ROOT}/nvar/src/nvARProxy.cpp"
  "${ARSDK_ROOT}/nvar/src/nvCVImageProxy.cpp")

target_include_directories(trackdub-maxine-lipsync-host PRIVATE
  "${ARSDK_ROOT}/nvar/include"
  "${ARSDK_ROOT}/features/nvarlipsync/include")

if(MSVC)
  target_compile_definitions(trackdub-maxine-lipsync-host PRIVATE _CRT_SECURE_NO_WARNINGS UNICODE _UNICODE)
  target_compile_options(trackdub-maxine-lipsync-host PRIVATE /W4 /utf-8)
else()
  target_link_libraries(trackdub-maxine-lipsync-host PRIVATE ${CMAKE_DL_LIBS})
endif()
