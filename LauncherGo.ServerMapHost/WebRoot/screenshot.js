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
    const url = URL.createObjectURL(blob);
    try { return await decodeUrl(url, signal); }
    finally { URL.revokeObjectURL(url); }
  }
  async function decodeUrl(url, signal) {
    signal.throwIfAborted();
    const img = new Image(); img.src = url;
    await img.decode(); signal.throwIfAborted(); return img;
  }
  function hasPixels(ctx, width, height) {
    // Missing/hidden tiles are returned as transparent PNGs with HTTP 200.
    // Check alpha, not the HTTP status or a filled background. Read strips to
    // avoid allocating another full 128 MB buffer for a maximum-size export.
    for (let y = 0; y < height; y += 64) {
      const rgba = ctx.getImageData(0, y, width, Math.min(64, height - y)).data;
      for (let i = 3; i < rgba.length; i += 4) if (rgba[i]) return true;
    }
    return false;
  }
  async function capture({ map, bounds, tileLayer, nativeZoom, signal }) {
    const size = dimensions(map, bounds, nativeZoom), { left, top, width, height, ratio, outputWidth, outputHeight } = size;
    const start = map.project(map.containerPointToLatLng([left, top]), nativeZoom);
    const end = map.project(map.containerPointToLatLng([left + width, top + height]), nativeZoom);
    const tileScale = ratio / map.getZoomScale(nativeZoom), jobs = [], vectors = snapshotVectors(map);
    for (let y = Math.floor(start.y / 512); y < Math.ceil(end.y / 512); y++)
      for (let x = Math.floor(start.x / 512); x < Math.ceil(end.x / 512); x++)
        // getTileUrl takes Leaflet zoom, which LiveTileLayer converts to the
        // server's reversed pyramid zoom. Leaflet nativeZoom means server 0.
        jobs.push({ x, y, url: tileLayer.getTileUrl({ x, y, z: nativeZoom }) });
    const canvas = document.createElement('canvas'); canvas.width = outputWidth; canvas.height = outputHeight;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw Error('captureFailed');
    const probe = document.createElement('canvas'), probeCtx = probe.getContext('2d', { willReadFrequently: true });
    if (!probeCtx) throw Error('captureFailed');
    const callerSignal = signal, workers = new AbortController(), cancelWorkers = () => workers.abort(callerSignal.reason);
    callerSignal.addEventListener('abort', cancelWorkers, { once: true });
    if (callerSignal.aborted) cancelWorkers();
    signal = workers.signal;
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
      const tileWorkers = Array.from({ length: Math.min(6, jobs.length) }, async () => {
        while (next < jobs.length) {
          signal.throwIfAborted(); const job = jobs[next++];
          const response = await fetch(job.url, { signal, cache: 'no-store' });
          if (response.status === 404 || response.status === 204) { missingTiles++; continue; }
          if (!response.ok) throw Error('captureFailed');
          const image = await decode(await response.blob(), signal);
          probe.width = image.naturalWidth; probe.height = image.naturalHeight;
          probeCtx.drawImage(image, 0, 0);
          if (!hasPixels(probeCtx, probe.width, probe.height)) { missingTiles++; continue; }
          ctx.imageSmoothingEnabled = false;
          ctx.drawImage(image, (job.x * 512 - start.x) * tileScale, (job.y * 512 - start.y) * tileScale, 512 * tileScale, 512 * tileScale);
        }
      });
      const [svgUrl] = await Promise.all([overlayPromise, ...tileWorkers]);
      signal.throwIfAborted();
      if (!hasPixels(ctx, outputWidth, outputHeight)) throw Error('captureEmpty');
      ctx.save(); ctx.scale(ratio, ratio); ctx.translate(-left, -top); drawVectors(ctx, vectors); ctx.restore();
      const svg = new DOMParser().parseFromString(decodeURIComponent(svgUrl.split(',')[1]), 'image/svg+xml').documentElement;
      // Preserve the original map viewport before cropping. A 100% sized
      // foreignObject otherwise shrinks to the selection's viewBox and clips
      // labels positioned past that small rectangle's right/bottom edge.
      const content = svg.querySelector('foreignObject');
      content.setAttribute('width', svg.getAttribute('width')); content.setAttribute('height', svg.getAttribute('height'));
      svg.setAttribute('width', outputWidth); svg.setAttribute('height', outputHeight);
      svg.setAttribute('viewBox', `${left} ${top} ${width} ${height}`); svg.setAttribute('preserveAspectRatio', 'none');
      // Chromium treats a Blob SVG containing foreignObject as tainted when
      // drawn to canvas. The self-contained data URL retains PNG exportability.
      const overlay = await decodeUrl('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(new XMLSerializer().serializeToString(svg)), signal);
      ctx.imageSmoothingEnabled = true; ctx.drawImage(overlay, 0, 0, outputWidth, outputHeight);
      const blob = await new Promise((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(Error('captureFailed')), 'image/png'));
      signal.throwIfAborted();
      return { blob, width: outputWidth, height: outputHeight, missingTiles };
    } finally {
      // A failed fetch/export must also stop the other tile workers before
      // releasing their canvases. Closing the dialog cancels the same work.
      workers.abort(); callerSignal.removeEventListener('abort', cancelWorkers);
      canvas.width = canvas.height = probe.width = probe.height = 0;
    }
  }
  window.ServerMapScreenshot = { capture };
})();
