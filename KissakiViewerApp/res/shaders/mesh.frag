#version 330 core

in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uTex;
uniform bool      uHasTex;
uniform vec3      uLightDir;   // normalized, pointing toward the light source
uniform float     uAmbient;

out vec4 FragColor;

void main()
{
    vec4 base = uHasTex ? texture(uTex, vUV) : vec4(0.71, 0.71, 0.75, 1.0);

    // Alpha discard: bypasses the depth-sort problem entirely.
    // Discarded pixels write nothing to the depth buffer, so overlapping
    // transparent geometry (hair, cloth) is handled correctly per-pixel.
    if (base.a < 0.1) discard;

    vec3  N     = normalize(vNormal);
    float diff  = max(dot(N, uLightDir), 0.0);
    float back  = max(dot(-N, uLightDir), 0.0) * 0.4; // soft back-face fill
    float light = uAmbient + (1.0 - uAmbient) * max(diff, back);

    FragColor = vec4(base.rgb * light, base.a);
}
