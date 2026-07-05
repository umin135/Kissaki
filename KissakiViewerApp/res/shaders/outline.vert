#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;

uniform mat4  uMVP;
uniform float uScale;

void main()
{
    // Expand vertex outward along its normal (object space)
    gl_Position = uMVP * vec4(aPos + aNormal * uScale, 1.0);
}
