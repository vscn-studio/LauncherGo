/* Native terrain tiles plus the visible Leaflet overlays, exported as lossless PNG. */
(() => {
  const MAX_SIDE = 8192, MAX_PIXELS = 32 * 1024 * 1024;
  function dimensions(map, bounds, nativeZoom) {
    const a = map.latLngToContainerPoint(bounds.getNorthWest()), b = map.latLngToContainerPoint(bounds.getSouthEast());
    const left = Math.floor(Math.min(a.x, b.x)), top = Math.floor(Math.min(a.y, b.y));
    const width = Math.ceil(Math.max(a.x, b.x)) - left, height = Math.ceil(Math.max(a.y, b.y)) - top;
    const ratio = Math.max(2, window.devicePixelRatio || 1, map.getZoomScale(nativeZoom));
    const outputWidth = Math.ceil(width * ratio), outputHeight = Math.ceil(height * ratio);
    if (width < 2 || height < 2) throw Error('selectionSmall');
    if (outputWidth > MAX_SIDE || outputHeight > MAX_SIDE || outputWidth * outputHeight > MAX_PIXELS) throw Error('selectionLarge');
    return { left, top, width, height, ratio, outputWidth, outputHeight };
  }
  function snapshotVectors(map) {
    const commands = [];
    const points = values => values.map(value => Array.isArray(value) ? points(value) : map.latLngToContainerPoint(value));
    // Canvas draw order includes bringToFront/bringToBack. Keep vector geometry,
    // rather than enlarging the screen's already rasterized canvas.
    map.eachLayer(renderer => {
      if (!(renderer instanceof L.Canvas)) return;
      for (let node = renderer._drawFirst; node; node = node.next) {
        const layer = node.layer;
        if (!map.hasLayer(layer)) continue;
        commands.push({ options: { ...layer.options },
          polygon: layer instanceof L.Polygon,
          points: layer.getLatLngs ? points(layer.getLatLngs()) : null,
          center: layer.getLatLng ? map.latLngToContainerPoint(layer.getLatLng()) : null,
          radius: layer._radius, radiusY: layer._radiusY });
      }
    });
    return commands;
  }
  function drawVectors(ctx, commands) {
    for (const item of commands) {
      const o = item.options;
      ctx.save(); ctx.beginPath();
      function path(points) {
        if (!points.length) return;
        if (Array.isArray(points[0])) { points.forEach(path); return; }
        points.forEach((p, i) => i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y));
        if (item.polygon) ctx.closePath();
      }
      if (item.points) path(item.points);
      else if (item.center && item.radius > 0) ctx.ellipse(item.center.x, item.center.y, item.radius, item.radiusY || item.radius, 0, 0, 2 * Math.PI);
      if (o.fill) { ctx.globalAlpha = o.fillOpacity; ctx.fillStyle = o.fillColor || o.color; ctx.fill(o.fillRule || 'evenodd'); }
      if (o.stroke && o.weight) {
        ctx.globalAlpha = o.opacity; ctx.strokeStyle = o.color; ctx.lineWidth = o.weight;
        ctx.lineCap = o.lineCap || 'round'; ctx.lineJoin = o.lineJoin || 'round';
        ctx.setLineDash(Array.isArray(o.dashArray) ? o.dashArray : String(o.dashArray || '').split(/[ ,]+/).map(Number));
        ctx.lineDashOffset = Number(o.dashOffset) || 0; ctx.stroke();
      }
      ctx.restore();
    }
  }
  async function decode(blob, signal) {
    signal.throwIfAborted();
    const url = URL.createObjectURL(blob), img = new Image();
    try { img.src = url; await img.decode(); signal.throwIfAborted(); return img; }
    finally { URL.revokeObjectURL(url); }
  }
  async function capture({ map, bounds, tileLayer, nativeZoom, signal }) {
    const size = dimensions(map, bounds, nativeZoom), { left, top, width, height, ratio, outputWidth, outputHeight } = size;
    const start = map.project(map.containerPointToLatLng([left, top]), nativeZoom);
    const end = map.project(map.containerPointToLatLng([left + width, top + height]), nativeZoom);
    const tileScale = ratio / map.getZoomScale(nativeZoom), jobs = [], vectors = snapshotVectors(map);
    for (let y = Math.floor(start.y / 512); y < Math.ceil(end.y / 512); y++)
      for (let x = Math.floor(start.x / 512); x < Math.ceil(end.x / 512); x++)
        jobs.push({ x, y, url: tileLayer.getTileUrl({ x, y, z: 0 }) });
    const canvas = document.createElement('canvas'); canvas.width = outputWidth; canvas.height = outputHeight;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw Error('captureFailed');
    let next = 0, missingTiles = 0;
    // Snapshot visible DOM overlays before waiting for network I/O. Popups,
    // controls, draft selection and terrain are intentionally not in this pass.
    const overlayPromise = htmlToImage.toSvg(map.getContainer(), {
      width: map.getSize().x, height: map.getSize().y, skipFonts: true,
      style: { background: 'transparent', transform: 'none', cursor: 'default' },
      fetchRequestInit: { signal, cache: 'no-store' }, includeQueryParams: true,
      filter: node => !node.matches?.('canvas,.leaflet-tile-pane,.leaflet-control-container,.leaflet-popup-pane'),
    });
    try {
      const workers = Array.from({ length: Math.min(6, jobs.length) }, async () => {
        while (next < jobs.length) {
          signal.throwIfAborted(); const job = jobs[next++];
          const response = await fetch(job.url, { signal, cache: 'no-store' });
          if (response.status === 404 || response.status === 204) { missingTiles++; continue; }
          if (!response.ok) throw Error('captureFailed');
          const image = await decode(await response.blob(), signal);
          ctx.imageSmoothingEnabled = false;
          ctx.drawImage(image, (job.x * 512 - start.x) * tileScale, (job.y * 512 - start.y) * tileScale, 512 * tileScale, 512 * tileScale);
        }
      });
      const [svgUrl] = await Promise.all([overlayPromise, ...workers]);
      signal.throwIfAborted();
      ctx.save(); ctx.scale(ratio, ratio); ctx.translate(-left, -top); drawVectors(ctx, vectors); ctx.restore();
      const svg = new DOMParser().parseFromString(decodeURIComponent(svgUrl.split(',')[1]), 'image/svg+xml').documentElement;
      svg.setAttribute('width', outputWidth); svg.setAttribute('height', outputHeight);
      svg.setAttribute('viewBox', `${left} ${top} ${width} ${height}`); svg.setAttribute('preserveAspectRatio', 'none');
      const overlay = await decode(new Blob([new XMLSerializer().serializeToString(svg)], { type: 'image/svg+xml' }), signal);
      ctx.imageSmoothingEnabled = true; ctx.drawImage(overlay, 0, 0, outputWidth, outputHeight);
      const blob = await new Promise((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(Error('captureFailed')), 'image/png'));
      signal.throwIfAborted();
      return { blob, width: outputWidth, height: outputHeight, missingTiles };
    } finally { canvas.width = canvas.height = 0; }
  }
  window.ServerMapScreenshot = { capture };
})();
