export function isWarning(seam){return !!seam&&(seam.state==='Warning'||seam.partial===true||seam.Partial===true||seam.hasCollision===true||seam.HasCollision===true);}
export function warningReason(seam){if(!seam)return '';const reasons=seam.warningReasons||seam.WarningReasons||[];return [...new Set([seam.Reason||seam.reason||'',...reasons].filter(Boolean))].join(' · ');}
export function warningIds(program){return new Set((program?.seams||[]).filter(isWarning).map(s=>s.id));}
