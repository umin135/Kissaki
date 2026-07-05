#version 330 core

in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uTex;
uniform bool      uHasTex;
uniform vec3      uLightDir;     // unit vector FROM scene TOWARD light source
uniform float     uAmbient;
uniform float     uAlphaCutoff;  // 0.1 = hard mask, 0.01 = allow soft blend pass

out vec4 FragColor;

void main()
{
    vec4 base = uHasTex ? texture(uTex, vUV) : vec4(0.71, 0.71, 0.75, 1.0);

    if (base.a < uAlphaCutoff) discard;

    vec3  N     = normalize(vNormal);
    float diff  = max(dot(N, uLightDir), 0.0);
    float back  = max(dot(-N, uLightDir), 0.0) * 0.35;
    float light = uAmbient + (1.0 - uAmbient) * max(diff, back);

    FragColor = vec4(base.rgb * light, base.a);
}
