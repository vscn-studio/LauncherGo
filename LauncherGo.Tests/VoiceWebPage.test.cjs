const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

async function main() {
  const listener = net.createServer();
  await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  const root = path.resolve(__dirname, '..');
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'launchergo-voice-browser-'));
  const config = path.join(directory, 'config.json');
  const stop = path.join(directory, 'stop');
  await fs.writeFile(config, JSON.stringify({ ListenAddress: '127.0.0.1', ListenPort: port, BackendPort: 15082 }));
  const executable = process.env.VOICE_HOST || path.join(root, 'LauncherGo.VoiceHost/bin/Debug/net10.0/LauncherGo.VoiceHost.exe');
  const host = spawn(executable, ['--config', config, '--state', path.join(directory, 'state.json'), '--stop', stop], { windowsHide: true, stdio: 'ignore' });
  const hostExit = new Promise((resolve, reject) => { host.once('exit', resolve); host.once('error', reject); });
  let browser;
  try {
    const url = `http://127.0.0.1:${port}/`;
    for (let attempt = 0; ; attempt++) {
      try { if ((await fetch(url)).ok) break; } catch { }
      if (attempt >= 50) throw new Error('VoiceHost did not start');
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    browser = await chromium.launch({ headless: true, channel: 'msedge', args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] });
    for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }, { width: 320, height: 740 }]) {
      const context = await browser.newContext({ viewport, permissions: ['microphone'] });
      const page = await context.newPage();
      let socket, frames = 0, testFrames = 0, levels = 0, errors = [], expectedToken = 'test-credential';
      page.on('pageerror', error => errors.push(error.message));
      await page.route('**/backend-health', route => route.fulfill({ json: { connected: true } }));
      await page.routeWebSocket('**/voice', route => {
        socket = route;
        route.onMessage(message => {
          if (typeof message === 'string') {
            const hello = JSON.parse(message);
            if (hello.type === 'level') { assert.ok(hello.rms >= 0 && hello.rms <= 1); levels++; return; }
            assert.equal(hello.token, expectedToken);
            assert.equal(hello.sampleRate, 48000);
            route.send(JSON.stringify({ type: 'accepted', deviceToken: 'saved-device-credential' }));
          } else if (message.length === 1924) { assert.equal(message.readInt32LE(0), 7); testFrames++; }
          else { assert.equal(message.length, 1920); frames++; }
        });
      });
      await page.goto(url);
      await page.locator('.backdrop').evaluate(image => image.decode());
      assert.equal(await page.locator('img').evaluateAll(images => images.every(image => image.complete && image.naturalWidth > 0)), true, 'all local assets load');
      assert.match(await page.locator('main').evaluate(element => getComputedStyle(element).backdropFilter), /blur/);
      await fs.mkdir(path.join(root, 'TestResults/voice-glass'), { recursive: true });
      await page.screenshot({ path: path.join(root, `TestResults/voice-glass/idle-${viewport.width}.png`), fullPage: true });
      await page.locator('#token').fill('test-credential');
      await page.locator('#reveal').click();
      assert.equal(await page.locator('#token').getAttribute('type'), 'text');
      await page.locator('#reveal').click();
      assert.equal(await page.locator('#token').getAttribute('type'), 'password');
      await page.locator('#connect').click();
      await page.waitForFunction(() => document.querySelector('#token').value === '');
      await page.waitForTimeout(200);
      assert.equal(frames, 0, 'no audio before game permission');
      const control = (allowed, extra = {}) => socket.send(JSON.stringify({
        type: 'control', allowed, voiceActivation: false, threshold: .08, monitor: true, testId: 0,
        playerName: '山间旅人', playerUid: 'test-player-uid-0123456789', channelName: '探索小队', channelId: 'expedition',
        target: 'ProximityAndChannel', mode: 'Talk', range: 32, muted: false, ...extra
      }));
      control(true);
      await page.waitForTimeout(400);
      assert.ok(frames > 0, 'PTT press streams PCM');
      assert.ok(levels > 0, 'input levels reach the game');
      let refreshPrompt = false;
      page.once('dialog', async dialog => { assert.equal(dialog.type(), 'beforeunload'); refreshPrompt = true; await dialog.dismiss(); });
      await page.reload({ timeout: 3000 }).catch(error => assert.match(error.message, /ERR_ABORTED|Timeout/));
      assert.ok(refreshPrompt, 'connected refresh asks for confirmation');
      assert.equal(await page.locator('#connect').isDisabled(), true, 'canceling refresh preserves connection');
      control(true);
      const beforeCancel = frames;
      await page.waitForTimeout(200);
      assert.ok(frames > beforeCancel, 'canceling refresh preserves microphone capture');
      assert.equal(await page.locator('#player-name').textContent(), '山间旅人');
      assert.equal(await page.locator('.voice-info').count(), 0, 'removed voice detail panel stays absent');
      await page.waitForFunction(() => [...document.querySelectorAll('.bar')].some(bar => bar.getBoundingClientRect().height > 12));
      control(true);
      await page.screenshot({ path: path.join(root, `TestResults/voice-glass/connected-${viewport.width}.png`), fullPage: true });
      if (viewport.width > 480) assert.equal(await page.evaluate(() => document.documentElement.scrollHeight > innerHeight), false, 'desktop panel fits viewport');
      control(false, { playerName: '<img src=x onerror=alert(1)>', muted: true, target: 'SelectedChannel', voiceActivation: true });
      await page.waitForTimeout(100);
      assert.equal(await page.locator('#player-name img').count(), 0, 'player name is plain text');
      control(false);
      await page.waitForTimeout(120);
      const released = frames;
      await page.waitForTimeout(250);
      assert.equal(frames, released, 'PTT release stops audio');
      const idleLevels = levels;
      control(false, { testId: 7, voiceActivation: true, threshold: 1 });
      await page.waitForTimeout(350);
      assert.ok(testFrames > 0, 'test recording streams tagged PCM without PTT or VAD gating');
      assert.equal(frames, released, 'test recording never streams public voice frames');
      assert.ok(levels > idleLevels, 'levels remain available without PTT');
      control(false);
      await page.waitForTimeout(120);
      const endedTest = testFrames;
      await page.waitForTimeout(200);
      assert.equal(testFrames, endedTest, 'stopping test stops tagged PCM');
      control(true);
      await page.waitForTimeout(2800);
      const stale = frames;
      await page.waitForTimeout(250);
      assert.equal(frames, stale, 'stale game controls stop audio');
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      socket.close({ code: 1000, reason: 'credential revoked' });
      await page.waitForFunction(() => !document.querySelector('#connect').disabled && document.querySelector('#stop').disabled);
      assert.match(await page.locator('#error').textContent(), /重新获取凭证/);
      assert.equal(await page.locator('#player-name').textContent(), '尚未连接');
      assert.equal(await page.locator('#db').textContent(), '静音');
      assert.equal(await page.locator('.bar').evaluateAll(bars => bars.every(bar => bar.style.height === '4px')), true);
      expectedToken = 'saved-device-credential';
      await page.locator('#connect').click();
      await page.waitForFunction(() => document.querySelector('#token').value === '');
      assert.equal(await page.evaluate(() => localStorage.getItem('svc-device-token')), 'saved-device-credential', 'device credential reconnects without pasting a token');
      await page.locator('#stop').click();
      let idlePrompt = false;
      page.once('dialog', async dialog => { idlePrompt = true; await dialog.dismiss(); });
      await page.reload();
      assert.equal(idlePrompt, false, 'disconnected refresh needs no confirmation');
      assert.deepEqual(errors, []);
      console.log(`PASS ${viewport.width}px: pairing, PTT, device reconnect, cleanup, layout`);
      await context.close();
    }
  } finally {
    await browser?.close();
    await fs.writeFile(stop, 'stop');
    const forceStop = setTimeout(() => host.kill(), 5000);
    await hostExit;
    clearTimeout(forceStop);
    await fs.rm(directory, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
