#version 330 core
in  vec3 vWorldPos;

uniform vec3  uCamPos;
uniform float uFadeEnd;

out vec4 FragColor;

void main()
{
    float dist  = length(vWorldPos.xz - uCamPos.xz);
    float alpha = 1.0 - smoothstep(uFadeEnd * 0.25, uFadeEnd, dist);
    if (alpha <= 0.01) discard;
    FragColor = vec4(0.28, 0.28, 0.28, alpha * 0.65);
}
