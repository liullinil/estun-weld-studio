shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_back;

// Painted industrial castings are dielectric: their metal substrate must not
// tint the specular lobe. Keep the manufacturer's CAD colour and its normals.
uniform vec4 paint_color : source_color = vec4(0.6, 0.6, 0.6, 1.0);
uniform float finish : hint_range(0.15, 0.8) = 0.36;
uniform float coat : hint_range(0.0, 0.3) = 0.08;
varying vec3 casting_position;

float hash_corner(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

float smooth_grain(vec3 p) {
	vec3 cell = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash_corner(cell), hash_corner(cell + vec3(1, 0, 0)), f.x),
			mix(hash_corner(cell + vec3(0, 1, 0)), hash_corner(cell + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash_corner(cell + vec3(0, 0, 1)), hash_corner(cell + vec3(1, 0, 1)), f.x),
			mix(hash_corner(cell + vec3(0, 1, 1)), hash_corner(cell + vec3(1, 1, 1)), f.x), f.y), f.z);
}

void vertex() {
	casting_position = VERTEX;
}

void fragment() {
	// Orange-peel finish is visible only when the surface is sufficiently close.
	// Fade below a pixel instead of sampling subpixel noise that sparkles in motion.
	vec3 grain_position = casting_position * 1800.0;
	float footprint = max(length(dFdx(grain_position)), length(dFdy(grain_position)));
	float resolved = 1.0 - smoothstep(0.35, 1.1, footprint);
	float grain = 0.0;
	if (resolved > 0.001) {
		grain = (smooth_grain(grain_position) - 0.5) * resolved;
	}
	ALBEDO = paint_color.rgb;
	METALLIC = 0.0;
	SPECULAR = 0.5;
	float perceptual_roughness = finish + grain * 0.028;
	// Geometric specular antialiasing broadens unresolved normal changes without
	// blurring the image, changing CAD normals or softening the robot's silhouette.
	vec3 nx = dFdx(NORMAL);
	vec3 ny = dFdy(NORMAL);
	float normal_variance = min(0.06, 0.18 * (dot(nx, nx) + dot(ny, ny)));
	ROUGHNESS = clamp(sqrt(perceptual_roughness * perceptual_roughness + normal_variance), 0.2, 0.8);
	CLEARCOAT = coat;
	CLEARCOAT_ROUGHNESS = 0.4;
}
