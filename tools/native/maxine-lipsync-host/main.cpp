/*
 * Trackdub Maxine LipSync host.
 *
 * Loads NVIDIA Maxine AR SDK LipSync once and lip-syncs one speaker turn per request over
 * stdin/stdout, so Trackdub pays the model load once per run instead of once per turn.
 *
 * Derived from the LipSyncApp sample in NVIDIA-Maxine/AR-SDK-Samples, which carries this notice:
 *
 *   SPDX-FileCopyrightText: Copyright (c) 2025 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
 *   SPDX-License-Identifier: MIT
 *
 *   Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 *   and associated documentation files (the "Software"), to deal in the Software without
 *   restriction, including without limitation the rights to use, copy, modify, merge, publish,
 *   distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 *   Software is furnished to do so, subject to the following conditions:
 *
 *   The above copyright notice and this permission notice shall be included in all copies or
 *   substantial portions of the Software.
 *
 *   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 *   BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 *   NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 *   DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 *   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 *
 * Protocol (stdout carries protocol output only; diagnostics go to stderr):
 *   startup   host writes "TRACKDUB-MAXINE-LIPSYNC 1 READY\n"
 *   request   "RUN <width> <height> <fps> <language> <frames> <wav-path>\n", then <frames> raw BGR24
 *             frames (width*height*3 bytes each) on stdin
 *   response  exactly <frames> raw BGR24 frames on stdout, then "OK <frames> <ms>\n" or
 *             "ERR <message>\n" (frames are still written on error so framing never breaks)
 *   shutdown  "QUIT\n" or end of stdin
 * The WAV must be 16 kHz mono PCM16 (LipSync's required input format).
 */

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <limits>
#include <sstream>
#include <string>
#include <vector>

#ifdef _WIN32
#define NOMINMAX
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#endif

#include "nvAR.h"
#include "nvARLipSync.h"
#include "nvAR_defs.h"
#include "nvCVImage.h"

char* g_nvARSDKPath = nullptr;

namespace {

constexpr const char* kHandshake = "TRACKDUB-MAXINE-LIPSYNC 1 READY";

bool ReadExact(std::FILE* stream, void* buffer, size_t count) {
  size_t total = 0;
  while (total < count) {
    size_t read = std::fread(static_cast<char*>(buffer) + total, 1, count - total, stream);
    if (read == 0) return false;
    total += read;
  }
  return true;
}

bool WriteExact(std::FILE* stream, const void* buffer, size_t count) {
  return std::fwrite(buffer, 1, count, stream) == count;
}

bool ReadLine(std::string* line) {
  line->clear();
  int c;
  while ((c = std::fgetc(stdin)) != EOF) {
    if (c == '\n') return true;
    if (c != '\r') line->push_back(static_cast<char>(c));
  }
  return !line->empty();
}

// Paths arrive as UTF-8; Windows needs the wide API for anything outside the ANSI code page.
std::FILE* OpenUtf8(const std::string& path, const char* mode) {
#ifdef _WIN32
  int length = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
  if (length <= 0) return nullptr;
  std::wstring wide(static_cast<size_t>(length), L'\0');
  MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wide.data(), length);
  std::wstring wide_mode(mode, mode + std::strlen(mode));
  return _wfopen(wide.c_str(), wide_mode.c_str());
#else
  return std::fopen(path.c_str(), mode);
#endif
}

uint32_t ReadU32(const unsigned char* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<uint32_t>(p[3]) << 24); }
uint16_t ReadU16(const unsigned char* p) { return static_cast<uint16_t>(p[0] | (p[1] << 8)); }

// Reads a 16 kHz mono PCM16 WAV by walking its RIFF chunks.
bool ReadPcm16MonoWav(const std::string& path, unsigned expected_rate, std::vector<float>* samples, std::string* error) {
  std::FILE* file = OpenUtf8(path, "rb");
  if (!file) {
    *error = "cannot open audio file";
    return false;
  }

  std::vector<unsigned char> bytes;
  unsigned char chunk[65536];
  size_t read;
  while ((read = std::fread(chunk, 1, sizeof(chunk), file)) > 0) bytes.insert(bytes.end(), chunk, chunk + read);
  std::fclose(file);

  if (bytes.size() < 12 || std::memcmp(bytes.data(), "RIFF", 4) != 0 || std::memcmp(bytes.data() + 8, "WAVE", 4) != 0) {
    *error = "audio is not a RIFF/WAVE file";
    return false;
  }

  uint16_t format = 0, channels = 0, bits = 0;
  uint32_t rate = 0;
  size_t offset = 12;
  const unsigned char* data = nullptr;
  uint32_t data_size = 0;
  while (offset + 8 <= bytes.size()) {
    const unsigned char* header = bytes.data() + offset;
    uint32_t size = ReadU32(header + 4);
    const unsigned char* body = header + 8;
    if (offset + 8 + size > bytes.size()) size = static_cast<uint32_t>(bytes.size() - offset - 8);
    if (std::memcmp(header, "fmt ", 4) == 0 && size >= 16) {
      format = ReadU16(body);
      channels = ReadU16(body + 2);
      rate = ReadU32(body + 4);
      bits = ReadU16(body + 14);
    } else if (std::memcmp(header, "data", 4) == 0) {
      data = body;
      data_size = size;
    }
    offset += 8 + size + (size & 1);
  }

  if (format != 1 || bits != 16 || channels != 1 || rate != expected_rate || data == nullptr) {
    std::ostringstream message;
    message << "audio must be " << expected_rate << " Hz mono PCM16 (got format " << format << ", " << bits << "-bit, "
            << channels << " channel(s), " << rate << " Hz)";
    *error = message.str();
    return false;
  }

  samples->resize(data_size / 2);
  for (size_t i = 0; i < samples->size(); ++i) {
    samples->at(i) = static_cast<int16_t>(ReadU16(data + i * 2)) / 32768.0f;
  }
  return true;
}

class LipSyncHost {
 public:
  explicit LipSyncHost(std::string model_dir) : model_dir_(std::move(model_dir)) {}

  ~LipSyncHost() { Unload(); }

  // Reloads only when the frame rate or language changes, because both are load-time settings.
  // Otherwise resets the temporal state so turns never bleed into each other.
  bool Prepare(float fps, unsigned language, unsigned width, unsigned height, std::string* error) {
    if (handle_ == nullptr || fps != fps_ || language != language_) {
      Unload();
      if (!Load(fps, language, error)) return false;
    } else if (NvCV_Status status = NvAR_ResetState(handle_, nullptr); status != NVCV_SUCCESS) {
      return Fail(error, "NvAR_ResetState", status);
    }

    if (width != width_ || height != height_) {
      if (NvCV_Status status = NvCVImage_Alloc(&gpu_src_, width, height, NVCV_BGR, NVCV_U8, NVCV_CHUNKY, NVCV_GPU, 1);
          status != NVCV_SUCCESS)
        return Fail(error, "NvCVImage_Alloc (source)", status);
      if (NvCV_Status status = NvCVImage_Alloc(&gpu_dst_, width, height, NVCV_BGR, NVCV_U8, NVCV_CHUNKY, NVCV_GPU, 1);
          status != NVCV_SUCCESS)
        return Fail(error, "NvCVImage_Alloc (output)", status);
      if (NvCV_Status status = NvCVImage_Alloc(&cpu_dst_, width, height, NVCV_BGR, NVCV_U8, NVCV_CHUNKY, NVCV_CPU, 1);
          status != NVCV_SUCCESS)
        return Fail(error, "NvCVImage_Alloc (download)", status);
      width_ = width;
      height_ = height;
    }

    if (NvCV_Status status = NvAR_SetObject(handle_, NvAR_Parameter_Output(Image), &gpu_dst_, sizeof(NvCVImage));
        status != NVCV_SUCCESS)
      return Fail(error, "set output image", status);
    if (NvCV_Status status =
            NvAR_SetObject(handle_, NvAR_Parameter_Output(Activation), &activation_, sizeof(NvAR_LipSyncActivation));
        status != NVCV_SUCCESS)
      return Fail(error, "set activation output", status);
    if (NvCV_Status status = NvAR_SetU32Array(handle_, NvAR_Parameter_Output(Ready), &output_ready_, 1);
        status != NVCV_SUCCESS)
      return Fail(error, "set ready output", status);
    return true;
  }

  unsigned sample_rate() const { return sample_rate_; }
  unsigned initial_frames() const { return initial_frames_; }

  bool SetAudio(std::vector<float>* frame, std::string* error) {
    NvCV_Status status = NvAR_SetF32Array(handle_, NvAR_Parameter_Input(AudioFrameBuffer), frame->data(),
                                          static_cast<int>(frame->size()));
    return status == NVCV_SUCCESS || Fail(error, "set audio frame", status);
  }

  bool SetImage(unsigned char* bgr, std::string* error) {
    NvCVImage cpu_src;
    NvCVImage_Init(&cpu_src, width_, height_, static_cast<int>(width_ * 3), bgr, NVCV_BGR, NVCV_U8, NVCV_CHUNKY, NVCV_CPU);
    if (NvCV_Status status = NvCVImage_Transfer(&cpu_src, &gpu_src_, 1.0f, stream_, &staging_); status != NVCV_SUCCESS)
      return Fail(error, "upload frame", status);
    NvCV_Status status = NvAR_SetObject(handle_, NvAR_Parameter_Input(Image), &gpu_src_, sizeof(NvCVImage));
    return status == NVCV_SUCCESS || Fail(error, "set input image", status);
  }

  // Runs one frame. Returns true with *ready set when an output frame was produced.
  bool Run(bool* ready, std::string* error) {
    output_ready_ = 0;
    if (NvCV_Status status = NvAR_Run(handle_); status != NVCV_SUCCESS) return Fail(error, "NvAR_Run", status);
    *ready = output_ready_ != 0;
    return true;
  }

  bool DownloadOutput(unsigned char* bgr, std::string* error) {
    if (NvCV_Status status = NvCVImage_Transfer(&gpu_dst_, &cpu_dst_, 1.0f, stream_, &staging_); status != NVCV_SUCCESS)
      return Fail(error, "download frame", status);
    const size_t row = static_cast<size_t>(width_) * 3;
    for (unsigned y = 0; y < height_; ++y) {
      std::memcpy(bgr + y * row, static_cast<unsigned char*>(cpu_dst_.pixels) + static_cast<size_t>(y) * cpu_dst_.pitch, row);
    }
    return true;
  }

 private:
  bool Load(float fps, unsigned language, std::string* error) {
    if (NvCV_Status status = NvAR_Create(NvAR_Feature_LipSync, &handle_); status != NVCV_SUCCESS)
      return Fail(error, "NvAR_Create(LipSync)", status);
    if (NvCV_Status status = NvAR_CudaStreamCreate(&stream_); status != NVCV_SUCCESS)
      return Fail(error, "NvAR_CudaStreamCreate", status);
    if (NvCV_Status status = NvAR_SetCudaStream(handle_, NvAR_Parameter_Config(CUDAStream), stream_); status != NVCV_SUCCESS)
      return Fail(error, "set CUDA stream", status);
    if (NvCV_Status status = NvAR_SetString(handle_, NvAR_Parameter_Config(ModelDir), model_dir_.c_str());
        status != NVCV_SUCCESS)
      return Fail(error, "set model directory", status);
    if (NvCV_Status status = NvAR_SetU32(handle_, NvAR_Parameter_Config(Language), language); status != NVCV_SUCCESS)
      return Fail(error, "set language", status);
    if (NvCV_Status status = NvAR_SetF32(handle_, NvAR_Parameter_Config(VideoFPS), fps); status != NVCV_SUCCESS)
      return Fail(error, "set video fps", status);
    if (NvCV_Status status = NvAR_Load(handle_); status != NVCV_SUCCESS) return Fail(error, "NvAR_Load", status);
    if (NvCV_Status status = NvAR_GetU32(handle_, NvAR_Parameter_Config(SampleRate), &sample_rate_); status != NVCV_SUCCESS)
      return Fail(error, "get sample rate", status);
    if (NvCV_Status status = NvAR_GetU32(handle_, NvAR_Parameter_Config(NumInitialFrames), &initial_frames_);
        status != NVCV_SUCCESS)
      return Fail(error, "get initial frame count", status);
    fps_ = fps;
    language_ = language;
    width_ = height_ = 0;
    std::cerr << "maxine-lipsync-host: loaded LipSync (fps " << fps << ", language " << language << ", "
              << initial_frames_ << " initial frames)" << std::endl;
    return true;
  }

  void Unload() {
    if (handle_ != nullptr) {
      NvAR_Destroy(handle_);
      handle_ = nullptr;
    }
    if (stream_ != nullptr) {
      NvAR_CudaStreamDestroy(stream_);
      stream_ = nullptr;
    }
    width_ = height_ = 0;
  }

  static bool Fail(std::string* error, const char* step, NvCV_Status status) {
    *error = std::string(step) + " failed: " + NvCV_GetErrorStringFromCode(status);
    return false;
  }

  std::string model_dir_;
  NvAR_FeatureHandle handle_ = nullptr;
  CUstream stream_ = nullptr;
  float fps_ = 0.0f;
  unsigned language_ = std::numeric_limits<unsigned>::max();
  unsigned width_ = 0, height_ = 0;
  unsigned sample_rate_ = 16000;
  unsigned initial_frames_ = 14;
  unsigned output_ready_ = 0;
  NvAR_LipSyncActivation activation_ = {};
  NvCVImage gpu_src_, gpu_dst_, cpu_dst_, staging_;
};

struct Job {
  unsigned width = 0, height = 0, language = 0;
  float fps = 0.0f;
  size_t frames = 0;
  std::string wav_path;
};

bool ParseRun(const std::string& line, Job* job) {
  std::istringstream input(line);
  std::string verb;
  input >> verb >> job->width >> job->height >> job->fps >> job->language >> job->frames;
  if (verb != "RUN" || !input) return false;
  std::getline(input, job->wav_path);
  job->wav_path.erase(0, job->wav_path.find_first_not_of(' '));
  return job->width > 0 && job->height > 0 && job->width <= 4096 && job->height <= 2160 && job->fps > 0.0f &&
         job->frames > 0 && !job->wav_path.empty();
}

// Processes one request. Always consumes all input frames and writes exactly job.frames output
// frames, so a failure never desynchronizes the byte stream.
void HandleJob(LipSyncHost* host, const Job& job) {
  const auto started = std::chrono::steady_clock::now();
  const size_t frame_bytes = static_cast<size_t>(job.width) * job.height * 3;
  std::vector<unsigned char> input(frame_bytes), output(frame_bytes, 0), last_output(frame_bytes, 0);
  std::string error;
  bool ok = host->Prepare(job.fps, job.language, job.width, job.height, &error);

  std::vector<float> audio;
  if (ok) ok = ReadPcm16MonoWav(job.wav_path, host->sample_rate(), &audio, &error);

  size_t written = 0;
  bool have_output = false;
  const size_t total_steps = job.frames + (ok ? host->initial_frames() : 0);
  const double samples_per_frame = static_cast<double>(host->sample_rate()) / job.fps;
  size_t audio_end_prev = 0;

  for (size_t step = 0; step < total_steps || step < job.frames; ++step) {
    const bool has_input = step < job.frames;
    if (has_input && !ReadExact(stdin, input.data(), frame_bytes)) {
      std::cerr << "maxine-lipsync-host: stdin closed mid-request" << std::endl;
      std::exit(2);
    }

    if (ok) {
      size_t audio_end = static_cast<size_t>((step + 1) * samples_per_frame);
      std::vector<float> audio_frame(audio_end - audio_end_prev, 0.0f);
      for (size_t s = audio_end_prev; s < audio_end && s < audio.size(); ++s) audio_frame[s - audio_end_prev] = audio[s];
      audio_end_prev = audio_end;

      bool ready = false;
      ok = host->SetAudio(&audio_frame, &error) && (!has_input || host->SetImage(input.data(), &error)) &&
           host->Run(&ready, &error);
      if (ok && ready && written < job.frames) {
        ok = host->DownloadOutput(output.data(), &error);
        if (ok) {
          WriteExact(stdout, output.data(), frame_bytes);
          last_output = output;
          have_output = true;
          ++written;
        }
      }
    }
  }

  // Pad with the last produced frame (or black if none) so the reply always has job.frames frames.
  while (written < job.frames) {
    WriteExact(stdout, have_output ? last_output.data() : output.data(), frame_bytes);
    ++written;
  }

  const long long elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                   std::chrono::steady_clock::now() - started).count();
  if (ok) {
    std::fprintf(stdout, "OK %zu %lld\n", written, elapsed_ms);
  } else {
    std::replace(error.begin(), error.end(), '\n', ' ');
    std::fprintf(stdout, "ERR %s\n", error.c_str());
  }
  std::fflush(stdout);
}

}  // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
  _setmode(_fileno(stdin), _O_BINARY);
  _setmode(_fileno(stdout), _O_BINARY);
#endif
  std::string model_dir;
  for (int i = 1; i < argc; ++i) {
    std::string arg = argv[i];
    if (arg == "--protocol") {
      std::fprintf(stdout, "%s\n", kHandshake);
      return 0;
    }
    if (arg.rfind("--model_path=", 0) == 0) model_dir = arg.substr(13);
  }
  if (model_dir.empty()) {
    std::cerr << "usage: maxine-lipsync-host --model_path=<ARSDK>/bin/models" << std::endl;
    return 1;
  }

  NvAR_ConfigureLogger(1, "stderr", nullptr, nullptr);
  LipSyncHost host(model_dir);
  std::fprintf(stdout, "%s\n", kHandshake);
  std::fflush(stdout);

  std::string line;
  while (ReadLine(&line)) {
    if (line == "QUIT") break;
    if (line.empty()) continue;
    Job job;
    if (!ParseRun(line, &job)) {
      std::fprintf(stdout, "ERR bad request\n");
      std::fflush(stdout);
      continue;
    }
    HandleJob(&host, job);
  }
  return 0;
}
