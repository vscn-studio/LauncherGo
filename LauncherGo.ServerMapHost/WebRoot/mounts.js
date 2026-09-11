/* Fixed-camera model atlas: turn the model, never rotate an oblique picture like a sticker. */
(() => {
  const DIRECTIONS = 16, COLUMNS = 4;
  function direction(yaw) {
    const circle = Math.PI * 2, angle = ((yaw % circle) + circle) % circle;
    return Math.round(angle / circle * DIRECTIONS) % DIRECTIONS;
  }
  function valid(feature) {
    const p = feature?.properties, xy = feature?.geometry?.coordinates;
    return feature?.geometry?.type === 'Point' && /^\d+$/.test(String(feature.id))
      && xy?.length === 2 && xy.every(Number.isFinite) && /^[a-f0-9]{64}$/.test(p?.imageKey || '')
      && [p.worldSize, p.centerX, p.centerZ, p.yaw, p.displayScale].every(Number.isFinite)
      && p.directions === DIRECTIONS && p.displayScale >= 1 && p.displayScale <= 2
      && p.worldSize >= .01 && p.worldSize <= 320 && Math.abs(p.centerX) <= 100 && Math.abs(p.centerZ) <= 100;
  }
  function create(feature, { map, api, gameLatLng, nativeZoom, displayScale = () => 1, imageUrl }) {
    if (!valid(feature)) return null; // No icon placeholder when a model is unavailable.
    if (!map.getPane('mounts')) {
      const pane = map.createPane('mounts'); pane.style.zIndex = '450'; pane.style.pointerEvents = 'none';
    }
    const turn = document.createElement('div'), frame = document.createElement('div'), img = document.createElement('img');
    turn.className = 'mount-model-heading'; turn.style.cssText = 'position:absolute;left:0;top:0;transform-origin:0 0;pointer-events:none';
    frame.className = 'mount-model-frame'; frame.style.cssText = 'position:absolute;overflow:hidden;background:none;pointer-events:none';
    img.className = 'mount-model-image'; img.draggable = false; img.decoding = 'async';
    img.style.cssText = 'position:absolute;display:block;max-width:none;max-height:none;border:0;background:none;pointer-events:none';
    frame.append(img); turn.append(frame);
    const marker = L.marker(gameLatLng(...feature.geometry.coordinates), {
      pane: 'mounts', interactive: false, keyboard: false,
      icon: L.divIcon({ className: 'mount-model-anchor', iconSize: [0, 0], iconAnchor: [0, 0], html: turn })
    });
    let current = feature, source = '';
    function layout() {
      const p = current.properties, ratio = Math.pow(2, map.getZoom() - nativeZoom()), zoomScale = ratio * p.displayScale * displayScale();
      const size = p.worldSize * zoomScale, index = direction(p.yaw);
      turn.style.width = turn.style.height = frame.style.width = frame.style.height = `${size}px`;
      turn.style.left = `${(p.centerX - p.worldSize / 2) * zoomScale}px`;
      turn.style.top = `${(p.centerZ - p.worldSize / 2) * zoomScale}px`;
      img.style.width = img.style.height = `${size * COLUMNS}px`;
      img.style.left = `${-(index % COLUMNS) * size}px`;
      img.style.top = `${-Math.floor(index / COLUMNS) * size}px`;
      frame.dataset.direction = String(index);
      // Filter the already-clipped silhouette, so adjacent atlas cells cast no shadows.
      const shadow = Math.max(.35, Math.min(1.2, .65 * Math.sqrt(ratio)));
      turn.style.filter = `drop-shadow(0 ${shadow}px ${shadow}px rgba(0,0,0,.48))`;
    }
    marker.updateMount = next => {
      if (!valid(next)) return;
      current = next; marker.setLatLng(gameLatLng(...next.geometry.coordinates));
      const url = imageUrl ? imageUrl(next) : `${api}/mount-image?id=${encodeURIComponent(next.id)}&key=${next.properties.imageKey}`;
      if (source !== url) {
        source = url; img.style.visibility = 'hidden';
        img.onload = () => { img.style.visibility = img.naturalWidth === 1024 && img.naturalHeight === 1024 ? '' : 'hidden'; };
        img.onerror = () => { img.style.visibility = 'hidden'; source = ''; };
        img.src = url;
      }
      img.alt = next.properties.name || ''; layout();
    };
    marker.on('add', () => { layout(); map.on('zoom', layout); });
    marker.on('remove', () => map.off('zoom', layout));
    marker.updateMount(feature); return marker;
  }
  window.ServerMapMounts = { create, valid, direction };
})();
