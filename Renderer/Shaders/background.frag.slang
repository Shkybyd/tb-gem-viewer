#version 460

in vec3 vSkyLookupInterpolant;
out vec4 outputColor;

#include "common/utils.slang"
#include "common/view_constants.slang"

uniform bool g_bShowLightBackground = false;
uniform bool g_bShowSolidBackground = false;
uniform bool g_bShowLobbyBackground = false;
uniform bool g_bHasLobbyTexture = false;
uniform sampler2D g_tLobbyBackground; // SrgbRead(true)

#define PIh (PI / 2.0)
#define PI2t (TAU / 3.0)

// how many pillars are there
#define PILLARS 3.0

// polar coordinates from
// lat: 0...PI2
// lon: 0...PI
vec2 polarCoords(in vec3 xyz) {
    // it may be necessary in your projection to either:
    // a) rotate the projection by 90deg OR
    // b) swap xyz until the projection makes sense:
    float theta = atan(xyz.y, xyz.x);
    float rho = length(xyz);
    return vec2((theta + PIh) * 2.0, acos(xyz.z / rho));
}

// goes from 0...1:
// - is 1 when forward along x axes
// - is 0 when forward along y axes
// (with multiples by the amount of color changing pillars)
float latitude(float polar) {
    return PackToColor(sin(polar * PILLARS / 2.0));
}

vec3 Rgb(int r, int g, int b)
{
    return vec3(r, g, b) / 255.0;
}

void main() {
    if (g_bShowLobbyBackground) {
        vec2 uv = gl_FragCoord.xy * g_vInvViewportSize;
        vec3 color;

        if (g_bHasLobbyTexture) {
            // Cover the viewport with the image, keeping its aspect ratio
            vec2 textureSizeF = vec2(textureSize(g_tLobbyBackground, 0));
            float viewAspect = g_vViewportSize.x / g_vViewportSize.y;
            float textureAspect = textureSizeF.x / textureSizeF.y;
            vec2 scale = viewAspect > textureAspect ? vec2(1.0, textureAspect / viewAspect) : vec2(viewAspect / textureAspect, 1.0);
            vec2 textureUv = (uv - 0.5) * scale + 0.5;
            color = texture(g_tLobbyBackground, vec2(textureUv.x, 1.0 - textureUv.y)).rgb * 0.45;
        } else {
            // The violet stage of the Dota hero page. Away from the middle it only changes from top to
            // bottom, so the panels over the sides can match it by its color at the edge of the view.
            vec3 sides = mix(Rgb(8, 7, 14), Rgb(22, 18, 36), smoothstep(0.0, 1.0, uv.y));
            float acrossMiddle = 1.0 - smoothstep(0.04, 0.19, abs(uv.x - 0.5));
            float heightOfGlow = exp(-pow2((uv.y - 0.55) * 2.2));
            vec3 stage = mix(sides, Rgb(60, 46, 88), acrossMiddle * heightOfGlow * 0.85);

            // A darker floor with a pool of light where the hero stands
            float floorPool = (1.0 - smoothstep(0.0, 0.22, length((uv - vec2(0.5, 0.1)) * vec2(1.0, 2.6)))) * acrossMiddle;
            stage = mix(stage, Rgb(34, 26, 52), floorPool * 0.6);
            color = SrgbGammaToLinear(stage);
        }

        outputColor = vec4(color, 1.0);
        return;
    }

    if (g_bShowSolidBackground) {
        outputColor = g_bShowLightBackground ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 1.0, 1.0);
        return;
    }

    // polar coordinates in lat/lon
    vec2 polar = polarCoords(vSkyLookupInterpolant.xyz);

    // goes from 0...1 from bottom to up
    // if the sphere is upside down:
    // - do 1.0 - polar.y / PI
    float upAmount = 1.0 - polar.y / PI;

    // interpolation values to move from one phase to another
    // steeper transitions with higher powers
    float ground = pow2(smoothstep(0.1, 0.3, upAmount));
    float horizon = pow(smoothstep(0.3, 0.5, upAmount), 12.0);
    float sky = pow2(smoothstep(0.45, 0.6, upAmount));
    float highSky = pow(smoothstep(0.6, 0.85, upAmount), 3.0);

    vec3 vApp = Rgb(44, 49, 61);

    vec3 SKY_C = Rgb(158, 158, 158);
    vec3 SKY_ALT_C = Rgb(75, 75, 75);
    vec3 HORIZON_C = Rgb(55, 55, 55);
    vec3 HORIZON_ALT_C = Rgb(75, 75, 75);
    vec3 GROUND_C = Rgb(43, 43, 43);
    vec3 GROUND_ALT_C = Rgb(54, 50, 54);

    if (!g_bShowLightBackground)
    {
        SKY_C = vApp * 3;
        SKY_ALT_C = vApp * 2;
        HORIZON_C = vApp * 1;
        HORIZON_ALT_C = vApp * 1.2;
        GROUND_C = vApp * 0.8;
        GROUND_ALT_C = vApp * 0.9;
    }

    // mixing the alternative colors to go through their alt version
    // set the PI2t multiplication to vary the color phases
    vec3 skyColor = mix(SKY_C, SKY_ALT_C, latitude(polar.x + 0.0 * PI2t));
    vec3 groundColor = mix(GROUND_C, GROUND_ALT_C, latitude(polar.x + 1.0 * PI2t));
    vec3 horizonColor = mix(HORIZON_C, HORIZON_ALT_C, latitude(polar.x + 2.0 * PI2t));

    vec3 lowGroundToGround = mix(GROUND_C, groundColor, ground);
    vec3 groundToHorizon =  mix(lowGroundToGround, horizonColor, horizon);
    vec3 horizonToSky = mix(groundToHorizon, skyColor, sky);
    vec3 skyToHighSky = mix(horizonToSky, SKY_C, highSky);

    outputColor = vec4(SrgbGammaToLinear(skyToHighSky), 1.0);
}
