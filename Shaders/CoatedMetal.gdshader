shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
uniform vec4 paint_color : source_color = vec4(0.94,0.47,0.026,1.0);
uniform float metalness=0.46;
uniform float finish=0.28;
varying vec3 local;
float h(vec3 p){return fract(sin(dot(p,vec3(127.1,311.7,74.7)))*43758.5453);}
void vertex(){local=VERTEX;}
void fragment(){float n=h(floor(local*1900.0));ALBEDO=paint_color.rgb*(0.985+n*0.03);METALLIC=metalness;ROUGHNESS=finish+n*0.055;CLEARCOAT=0.24;CLEARCOAT_ROUGHNESS=0.32;}
