const paths = {
  axes:'<path d="M7 17V4m0 13h13M7 17l-4 4M7 4l-3 3m3-3 3 3m10 10-3-3m3 3-3 3"/>',
  trace:'<path d="M3 19h6V7h12"/><circle cx="3" cy="19" r="1.5"/><circle cx="9" cy="19" r="1.5"/><circle cx="9" cy="7" r="1.5"/><circle cx="21" cy="7" r="1.5"/>',
  shadows:'<path d="m12 3 8 5v8l-8 5-8-5V8Zm0 0v9m-8-4 8 4 8-4m-8 4v9"/><path d="m14 14 4-2v3l-4 3Z" fill="currentColor" stroke="none"/>',
  ao:'<circle cx="12" cy="10" r="6"/><path d="M3 18c3-4 15-4 18 0M5 21h14"/>',
  bloom:'<path d="m12 2 2.3 7.7L22 12l-7.7 2.3L12 22l-2.3-7.7L2 12l7.7-2.3Z"/>',
  sparks:'<path d="m13 2-6 11h5l-1 9 7-13h-5ZM3 6l2 2m15 8 2 2M2 16l3-1m14-9 3-2"/>',
  smoke:'<path d="M5 20h14M8 16c-6-5 5-6 1-12m4 12c-6-5 5-6 1-12m4 12c-4-4 4-5 2-9"/>',
  beads:'<path d="M3 18 21 6"/><ellipse cx="6" cy="16" rx="3" ry="2" transform="rotate(-34 6 16)"/><ellipse cx="12" cy="12" rx="3" ry="2" transform="rotate(-34 12 12)"/><ellipse cx="18" cy="8" rx="3" ry="2" transform="rotate(-34 18 8)"/>',
  exposure:'<circle cx="12" cy="12" r="4"/><path d="M12 1v3m0 16v3M1 12h3m16 0h3M4.2 4.2l2.1 2.1m11.4 11.4 2.1 2.1m0-15.6-2.1 2.1M6.3 17.7l-2.1 2.1"/>',
  resolution:'<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M8 22h8m-4-4v4M7 8h3m-3 0v3m10 3h-3m3 0v-3"/>',
  reset:'<path d="M4 9a8 8 0 1 1 0 7M4 3v6h6"/>'
};
export function icon(name){return `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${paths[name]}</svg>`;}
export function iconButton(id,name,label,active=false){return `<button id="${id}" class="round-icon${active?' active':''}" type="button" title="${label}" aria-label="${label}"${['exposure','resolution','reset'].includes(name)?'':` aria-pressed="${active}"`}>${icon(name)}</button>`;}
export const displayControls=[['shadows','Studio shadows'],['ao','Ambient occlusion'],['bloom','Bloom'],['sparks','Welding sparks'],['smoke','Welding smoke'],['beads','Weld beads']];
