#version 330 core

in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uTex;
uniform bool      uHasTex;
uniform vec3      uLightDir;   // unit vector FROM scene TOWARD light source
uniform float     uAmbient;
uniform bool      uDither;     // true = Bayer dither discard (alpha submeshes)

out vec4 FragColor;

// 4x4 Bayer ordered dithering matrix (screen-space)
float BayerDither(vec2 fragCoord)
{
    const float M[16] = float[](
         0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
        12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
         3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
        15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
    );
    int x = int(mod(fragCoord.x, 4.0));
    int y = int(mod(fragCoord.y, 4.0));
    return M[y * 4 + x];
}

void main()
{
    vec4 base = uHasTex ? texture(uTex, vUV) : vec4(0.71, 0.71, 0.75, 1.0);

    if (uDither)
    {
        // Dithered mask: alpha drives pixel density, no depth-sort needed
        if (base.a < BayerDither(gl_FragCoord.xy)) discard;
    }
    else
    {
        if (base.a < 0.1) discard;
    }

    vec3  N     = normalize(vNormal);
    float diff  = max(dot(N, uLightDir), 0.0);
    float back  = max(dot(-N, uLightDir), 0.0) * 0.35;
    float light = uAmbient + (1.0 - uAmbient) * max(diff, back);

    FragColor = vec4(base.rgb * light, 1.0);
}
