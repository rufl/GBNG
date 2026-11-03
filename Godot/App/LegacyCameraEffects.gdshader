shader_type canvas_item;
render_mode unshaded, blend_disabled;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform bool fx_pixelate = false;
uniform bool fx_film = false;
uniform bool fx_bw = false;
uniform bool fx_saturate = false;
uniform bool fx_vhs = false;
uniform bool fx_krazy = false;
uniform bool fx_drawing = false;

float luminance(vec3 color) {
    return dot(color, vec3(0.2126, 0.7152, 0.0722));
}

float noise(vec2 point) {
    return fract(sin(dot(point, vec2(127.1, 311.7))) * 43758.5453123);
}

void fragment() {
    vec2 resolution = vec2(textureSize(screen_texture, 0));
    vec2 texel = 1.0 / resolution;
    vec2 uv = SCREEN_UV;

    if (fx_pixelate) {
        float block_size = 7.0;
        uv = (floor(uv * resolution / block_size) + 0.5) * block_size / resolution;
    }

    vec3 color = texture(screen_texture, uv).rgb;

    if (fx_film) {
        float grain = noise(floor(uv * resolution) + floor(TIME * 48.0));
        float vignette = 1.0 - smoothstep(0.28, 0.95, length((uv - 0.5) * vec2(1.0, 0.82)));
        color = mix(color, color * vec3(1.10, 0.99, 0.84), 0.25);
        color = color * mix(0.78, 1.0, vignette) + (grain - 0.5) * 0.055;
    }

    if (fx_bw) {
        float gray = luminance(color);
        color = vec3(gray);
    }

    if (fx_saturate) {
        float gray = luminance(color);
        color = mix(vec3(gray), color, 1.65);
    }

    if (fx_vhs) {
        float line = floor(uv.y * resolution.y);
        float jitter = (noise(vec2(line, floor(TIME * 15.0))) - 0.5) * texel.x * 10.0;
        vec2 distorted_uv = clamp(uv + vec2(jitter, sin(uv.y * 31.0 + TIME * 2.0) * texel.y * 1.2), vec2(0.0), vec2(1.0));
        vec3 tape = vec3(
            texture(screen_texture, clamp(distorted_uv + vec2(texel.x * 1.8, 0.0), vec2(0.0), vec2(1.0))).r,
            texture(screen_texture, distorted_uv).g,
            texture(screen_texture, clamp(distorted_uv - vec2(texel.x * 1.8, 0.0), vec2(0.0), vec2(1.0))).b
        );
        float scanline = 0.93 + 0.07 * sin(uv.y * resolution.y * 3.14159265);
        color = mix(color, tape * scanline, 0.78);
    }

    if (fx_krazy) {
        float wave = sin((uv.x + uv.y) * 24.0 + TIME * 3.2) * 0.035;
        float hue = TIME * 0.6 + uv.y * 2.0 + wave;
        vec3 psychedelic = 0.5 + 0.5 * cos(vec3(0.0, 2.094, 4.188) + hue);
        color = mix(color, color * psychedelic * 1.8, 0.58);
    }

    if (fx_drawing) {
        float northwest = luminance(texture(screen_texture, clamp(uv + texel * vec2(-1.0, -1.0), vec2(0.0), vec2(1.0))).rgb);
        float north = luminance(texture(screen_texture, clamp(uv + texel * vec2(0.0, -1.0), vec2(0.0), vec2(1.0))).rgb);
        float northeast = luminance(texture(screen_texture, clamp(uv + texel * vec2(1.0, -1.0), vec2(0.0), vec2(1.0))).rgb);
        float west = luminance(texture(screen_texture, clamp(uv + texel * vec2(-1.0, 0.0), vec2(0.0), vec2(1.0))).rgb);
        float east = luminance(texture(screen_texture, clamp(uv + texel * vec2(1.0, 0.0), vec2(0.0), vec2(1.0))).rgb);
        float southwest = luminance(texture(screen_texture, clamp(uv + texel * vec2(-1.0, 1.0), vec2(0.0), vec2(1.0))).rgb);
        float south = luminance(texture(screen_texture, clamp(uv + texel * vec2(0.0, 1.0), vec2(0.0), vec2(1.0))).rgb);
        float southeast = luminance(texture(screen_texture, clamp(uv + texel * vec2(1.0, 1.0), vec2(0.0), vec2(1.0))).rgb);
        float edge_x = -northwest - 2.0 * west - southwest + northeast + 2.0 * east + southeast;
        float edge_y = -northwest - 2.0 * north - northeast + southwest + 2.0 * south + southeast;
        float ink = clamp(length(vec2(edge_x, edge_y)) * 2.4, 0.0, 1.0);
        float paper_grain = (noise(floor(uv * resolution / 3.0)) - 0.5) * 0.07;
        vec3 paper = vec3(0.94, 0.91, 0.83) + paper_grain;
        vec3 drawing = mix(paper, vec3(0.12, 0.10, 0.09), ink);
        color = mix(color, drawing, 0.9);
    }

    COLOR = vec4(clamp(color, vec3(0.0), vec3(1.0)), 1.0);
}
