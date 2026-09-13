shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
uniform vec3 tint : source_color = vec3(0.23, 0.25, 0.27);
varying vec3 wp;
float hash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}
float noise(vec2 p){vec2 i=floor(p);vec2 f=fract(p);vec2 u=f*f*(3.0-2.0*f);return mix(mix(hash(i),hash(i+vec2(1,0)),u.x),mix(hash(i+vec2(0,1)),hash(i+vec2(1,1)),u.x),u.y);}
void vertex(){wp=(MODEL_MATRIX*vec4(VERTEX,1.0)).xyz;}
void fragment(){
 vec2 p=wp.xz;
 float broad=noise(p*1.7)*0.55+noise(p*9.0)*0.25+noise(p*85.0)*0.2;
 vec2 seam=abs(fract((p+0.01)/3.0)-0.5);
 float line=smoothstep(0.496,0.499,max(seam.x,seam.y));
 float detail=1.0-smoothstep(0.001,0.012,max(length(dFdx(p)),length(dFdy(p))));
 float fleck=mix(0.5,noise(p*220.0),detail);
 ALBEDO=tint*(0.80+broad*0.35)*(1.0-line*0.32);
 ROUGHNESS=clamp(0.61+broad*0.13+fleck*0.03,0.0,1.0);
 METALLIC=0.0;
 NORMAL_MAP=vec3(0.5+(noise(p*60.0)-0.5)*0.04*detail,0.5+(noise(p.yx*60.0)-0.5)*0.04*detail,1.0);
 NORMAL_MAP_DEPTH=0.22;
}
