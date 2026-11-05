@tool

extends CompositorEffect

const LOCAL_SIZE := 8
const SCRATCH_CONTEXT := "legacy_motion_blur"
const SCRATCH_NAME := "blurred_color"
const SHADER_CODE := """#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D source_color;
layout(set = 0, binding = 1) uniform sampler2D velocity_texture;
layout(rgba16f, set = 0, binding = 2) uniform image2D destination_color;

layout(push_constant, std430) uniform BlurParameters {
    int copy_only;
    int padding_0;
    int padding_1;
    int padding_2;
} parameters;

const int SAMPLE_COUNT = 9;
const float MAX_TRAIL_PIXELS = 16.0;
const float SHUTTER_FRACTION = 0.5;

void main() {
    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(destination_color);
    if (pixel.x >= size.x || pixel.y >= size.y) {
        return;
    }

    if (parameters.copy_only != 0) {
        imageStore(destination_color, pixel, texelFetch(source_color, pixel, 0));
        return;
    }

    vec2 texel_size = 1.0 / vec2(size);
    vec2 uv = (vec2(pixel) + vec2(0.5)) * texel_size;
    vec2 trail = texture(velocity_texture, uv).xy * SHUTTER_FRACTION;
    float trail_pixels = length(trail * vec2(size));
    if (trail_pixels < 0.5) {
        imageStore(destination_color, pixel, texelFetch(source_color, pixel, 0));
        return;
    }
    if (trail_pixels > MAX_TRAIL_PIXELS) {
        trail *= MAX_TRAIL_PIXELS / trail_pixels;
    }

    vec2 half_texel = texel_size * 0.5;
    vec2 min_uv = half_texel;
    vec2 max_uv = vec2(1.0) - half_texel;
    vec4 accumulated = vec4(0.0);
    for (int sample_index = 0; sample_index < SAMPLE_COUNT; sample_index++) {
        float amount = float(sample_index) / float(SAMPLE_COUNT - 1);
        vec2 sample_uv = clamp(uv + trail * amount, min_uv, max_uv);
        accumulated += texture(source_color, sample_uv);
    }
    imageStore(destination_color, pixel, accumulated / float(SAMPLE_COUNT));
}
"""

var _render_device: RenderingDevice
var _shader := RID()
var _pipeline := RID()
var _sampler := RID()
var _blur_push_constant: PackedByteArray = PackedInt32Array([0, 0, 0, 0]).to_byte_array()
var _copy_push_constant: PackedByteArray = PackedInt32Array([1, 0, 0, 0]).to_byte_array()
var _blur_uniform_set := RID()
var _copy_uniform_set := RID()
var _bound_source := RID()
var _bound_velocity := RID()
var _bound_scratch := RID()
var _render_state_mutex := Mutex.new()
var _has_rendered_frame := false

func _init() -> void:
    effect_callback_type = EFFECT_CALLBACK_TYPE_POST_SKY
    access_resolved_color = true
    needs_motion_vectors = true
    _render_device = RenderingServer.get_rendering_device()
    if _render_device == null:
        push_error("Motion blur requires a RenderingDevice.")
        return

    var shader_source := RDShaderSource.new()
    shader_source.language = RenderingDevice.SHADER_LANGUAGE_GLSL
    shader_source.source_compute = SHADER_CODE
    var shader_spirv: RDShaderSPIRV = _render_device.shader_compile_spirv_from_source(shader_source)
    if not shader_spirv.compile_error_compute.is_empty():
        push_error("The motion-blur compute shader did not compile: " + shader_spirv.compile_error_compute)
        return

    _shader = _render_device.shader_create_from_spirv(shader_spirv)
    if not _shader.is_valid():
        push_error("The motion-blur compute shader could not be loaded.")
        return
    _pipeline = _render_device.compute_pipeline_create(_shader)
    if not _pipeline.is_valid():
        push_error("The motion-blur compute pipeline could not be created.")
        return

    var sampler_state := RDSamplerState.new()
    sampler_state.min_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
    sampler_state.mag_filter = RenderingDevice.SAMPLER_FILTER_LINEAR
    sampler_state.repeat_u = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
    sampler_state.repeat_v = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
    sampler_state.repeat_w = RenderingDevice.SAMPLER_REPEAT_MODE_CLAMP_TO_EDGE
    _sampler = _render_device.sampler_create(sampler_state)
    if not _sampler.is_valid():
        push_error("The motion-blur texture sampler could not be created.")

func _notification(what: int) -> void:
    if what == NOTIFICATION_PREDELETE:
        release_gpu_resources()

func release_gpu_resources() -> void:
    if _render_device == null:
        return
    var render_device := _render_device
    _render_device = null
    if _shader.is_valid():
        render_device.free_rid(_shader)
    if _sampler.is_valid():
        render_device.free_rid(_sampler)
    _shader = RID()
    _pipeline = RID()
    _sampler = RID()

func is_ready() -> bool:
    return _render_device != null and _shader.is_valid() and _pipeline.is_valid() and _sampler.is_valid()

func has_rendered_frame() -> bool:
    _render_state_mutex.lock()
    var rendered := _has_rendered_frame
    _render_state_mutex.unlock()
    return rendered

func _render_callback(callback_type: int, render_data: RenderData) -> void:
    if callback_type != EFFECT_CALLBACK_TYPE_POST_SKY or not is_ready():
        return

    var buffers := render_data.get_render_scene_buffers() as RenderSceneBuffersRD
    if buffers == null or buffers.get_view_count() != 1:
        return
    var size: Vector2i = buffers.get_internal_size()
    if size.x <= 0 or size.y <= 0:
        return

    var source_color: RID = buffers.get_color_layer(0, false)
    var velocity: RID = buffers.get_velocity_layer(0, false)
    if not source_color.is_valid() or not velocity.is_valid():
        return

    var scratch: RID = buffers.create_texture(
        SCRATCH_CONTEXT,
        SCRATCH_NAME,
        RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT,
        RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_STORAGE_BIT,
        RenderingDevice.TEXTURE_SAMPLES_1,
        size,
        1,
        1,
        false,
        false
    )
    if not scratch.is_valid():
        return

    if source_color != _bound_source or velocity != _bound_velocity or scratch != _bound_scratch or not _blur_uniform_set.is_valid() or not _copy_uniform_set.is_valid():
        _blur_uniform_set = _create_uniform_set(source_color, velocity, scratch)
        _copy_uniform_set = _create_uniform_set(scratch, velocity, source_color)
        if not _blur_uniform_set.is_valid() or not _copy_uniform_set.is_valid():
            return
        _bound_source = source_color
        _bound_velocity = velocity
        _bound_scratch = scratch

    var compute_list := _render_device.compute_list_begin()
    _render_device.compute_list_bind_compute_pipeline(compute_list, _pipeline)
    _render_device.compute_list_bind_uniform_set(compute_list, _blur_uniform_set, 0)
    _render_device.compute_list_set_push_constant(compute_list, _blur_push_constant, _blur_push_constant.size())
    _render_device.compute_list_dispatch(compute_list, ceili(float(size.x) / LOCAL_SIZE), ceili(float(size.y) / LOCAL_SIZE), 1)
    _render_device.compute_list_add_barrier(compute_list)
    _render_device.compute_list_bind_uniform_set(compute_list, _copy_uniform_set, 0)
    _render_device.compute_list_set_push_constant(compute_list, _copy_push_constant, _copy_push_constant.size())
    _render_device.compute_list_dispatch(compute_list, ceili(float(size.x) / LOCAL_SIZE), ceili(float(size.y) / LOCAL_SIZE), 1)
    _render_device.compute_list_end()

    _render_state_mutex.lock()
    _has_rendered_frame = true
    _render_state_mutex.unlock()

func _create_uniform_set(source: RID, velocity: RID, output: RID) -> RID:
    var source_uniform := RDUniform.new()
    source_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
    source_uniform.binding = 0
    source_uniform.add_id(_sampler)
    source_uniform.add_id(source)

    var velocity_uniform := RDUniform.new()
    velocity_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_SAMPLER_WITH_TEXTURE
    velocity_uniform.binding = 1
    velocity_uniform.add_id(_sampler)
    velocity_uniform.add_id(velocity)

    var output_uniform := RDUniform.new()
    output_uniform.uniform_type = RenderingDevice.UNIFORM_TYPE_IMAGE
    output_uniform.binding = 2
    output_uniform.add_id(output)

    var uniforms: Array[RDUniform] = [source_uniform, velocity_uniform, output_uniform]
    return UniformSetCacheRD.get_cache(_shader, 0, uniforms)
