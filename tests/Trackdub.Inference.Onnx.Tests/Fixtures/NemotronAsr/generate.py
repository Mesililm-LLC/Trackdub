"""Regenerate the tiny synthetic CPU fixtures. Development only: requires onnx.

No pretrained weights or external data. Tests consume the checked-in models and
do not need Python. The encoder emits one frame; the decoder always emits blank.
"""

from pathlib import Path

import onnx


MODELS = {
    "managed-encoder.onnx": """
<ir_version: 9, opset_import: ["" : 13]>
"nemotron-managed-encoder" (
    float[1,128,65] processed_signal, int64[1] processed_signal_length,
    float[1,1,2,2] cache_last_channel, float[1,1,2,1] cache_last_time,
    int64[1] cache_last_channel_len
) => (
    float[1,2,1] encoded, int64[1] encoded_len,
    float[1,1,2,2] cache_last_channel_next,
    float[1,1,2,1] cache_last_time_next, int64[1] cache_last_channel_len_next
) <float[1,2,1] encoded = {0,0}, int64[1] encoded_len = {1}>
{
    cache_last_channel_next = Identity(cache_last_channel)
    cache_last_time_next = Identity(cache_last_time)
    cache_last_channel_len_next = Identity(cache_last_channel_len)
}
""",
    "managed-decoder.onnx": """
<ir_version: 9, opset_import: ["" : 13]>
"nemotron-managed-decoder" (
    float[1,2,1] encoder_outputs, int64[1,1] targets, int64[1] target_length,
    float[1,1,2] input_states_1, float[1,1,2] input_states_2
) => (
    float[1,1,2] outputs, int64[1] prednet_lengths,
    float[1,1,2] output_states_1, float[1,1,2] output_states_2
) <float[1,1,2] outputs = {0,1}, int64[1] prednet_lengths = {1}>
{
    output_states_1 = Identity(input_states_1)
    output_states_2 = Identity(input_states_2)
}
""",
}


if __name__ == "__main__":
    for filename, source in MODELS.items():
        model = onnx.parser.parse_model(source)
        onnx.checker.check_model(model)
        onnx.save_model(model, Path(__file__).parent / filename)
