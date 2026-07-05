#version 330 core
in  vec3 vWorldPos;

uniform vec3  uCamPos;
uniform float uFadeEnd;

out vec4 FragColor;

// 4x4 Bayer dither for the distance fade — idempotent (same result if drawn twice)
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
    float dist  = length(vWorldPos.xz - uCamPos.xz);
    float alpha = 1.0 - smoothstep(uFadeEnd * 0.25, uFadeEnd, dist);

    // Discard-based fade: no blending needed, safe against double-render
    if (alpha < BayerDither(gl_FragCoord.xy)) discard;

    FragColor = vec4(0.14, 0.14, 0.14, 1.0);
}
