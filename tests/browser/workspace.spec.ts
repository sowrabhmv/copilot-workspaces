import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

type WorkspaceSnapshot = {
  id: string;
  status: string;
  layout: { positions: Record<string, { x: number; y: number }>; viewport: { x: number; y: number; zoom: number } };
  clarificationId: string | null;
  clarification: null | {
    elements: Record<string, {
      type: string;
      props: {
        label?: string;
        required?: boolean;
        options?: { id: string; label: string }[];
        value?: string | number | boolean | string[] | null;
        min?: number | string | null;
        step?: number;
      };
    }>;
  };
  agents: { id: string; name: string; role: string; assigned: boolean; sessionId: string | null }[];
  jobs: { id: string; kind: string; status: string; errorCode: string | null }[];
  feedback: { text: string; revision: number | null; status: string; elementId: string | null }[];
  artifacts: {
    id: string;
    title: string;
    currentRevision: number;
    acceptedRevision: number | null;
    revisions: {
      revision: number;
      source: string;
      status: string;
      review: string | null;
      spec: { elements: Record<string, { type: string; props: { text?: string } }> };
    }[];
  }[];
};

async function workspace(request: APIRequestContext, id: string): Promise<WorkspaceSnapshot> {
  const response = await request.get(`/api/workspaces/${id}`);
  expect(response.ok()).toBeTruthy();
  return response.json() as Promise<WorkspaceSnapshot>;
}

async function waitForWorkspace(
  request: APIRequestContext,
  id: string,
  predicate: (value: WorkspaceSnapshot) => boolean,
): Promise<WorkspaceSnapshot> {
  await expect.poll(async () => {
    const value = await workspace(request, id);
    expect(value.jobs.filter(job => job.status === 'failed')).toEqual([]);
    return predicate(value);
  }, { timeout: 25_000 }).toBe(true);
  return workspace(request, id);
}

async function focusView(page: Page) {
  await page.getByRole('button', { name: 'Activity', exact: true }).waitFor();
  const control = page.getByRole('button', { name: 'Focus view', exact: true });
  if (await control.isVisible()) await control.click();
}

async function demoFixture(request: APIRequestContext, objective: string, stopAtQuestions = false) {
  const bootstrap = await (await request.get('/api/bootstrap')).json() as { csrfToken: string };
  const headers = { 'X-Workspace-Token': bootstrap.csrfToken };
  const response = await request.post('/api/workspaces', { headers, data: { objective, provider: 'demo' } });
  expect(response.status()).toBe(201);
  const created = await response.json() as WorkspaceSnapshot;
  const asking = await waitForWorkspace(request, created.id,
    value => value.clarification !== null || value.artifacts.length > 0);
  if (asking.artifacts.length > 0 || stopAtQuestions) return asking;
  const answers = answersFor(asking.clarification!);
  const answered = await request.post(`/api/workspaces/${created.id}/answers`, {
    headers, data: { clarificationId: asking.clarificationId, answers },
  });
  expect(answered.ok()).toBeTruthy();
  return waitForWorkspace(request, created.id, value => value.artifacts.length > 0);
}

function answersFor(spec: NonNullable<WorkspaceSnapshot['clarification']>): Record<string, string> {
  return Object.fromEntries(Object.entries(spec.elements).filter(([, element]) => element.type.endsWith('Question'))
    .map(([id, element]) => {
      const { value, min, options } = element.props;
      switch (element.type) {
        case 'ChoiceQuestion': return [id, typeof value === 'string' ? value : options![0].id];
        case 'MultiChoiceQuestion': return [id, JSON.stringify(Array.isArray(value) ? value : [options![0].id])];
        case 'SliderQuestion':
        case 'NumberQuestion': return [id, String(value ?? min ?? 0)];
        case 'ToggleQuestion': return [id, String(value)];
        case 'DateQuestion': return [id, String(value ?? min ?? '2026-10-01')];
        default: return [id, 'Keep the scope small and preserve human review.'];
      }
    }));
}

function textElement(value: WorkspaceSnapshot) {
  const artifact = value.artifacts[0];
  const current = artifact.revisions.find(revision => revision.revision === artifact.currentRevision)!;
  return Object.entries(current.spec.elements).find(([, element]) => element.type === 'Text')![0];
}

test('human-led canvas completes clarification, concurrent feedback, review, edits, and reload', async ({ page, request }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'What should we get done?' })).toBeVisible();
  await expect(page.locator('.provider-mode')).toContainText('Auto');
  await page.getByRole('button', { name: 'Try a demo workspace', exact: true }).click();
  await page.waitForURL(/#\/workspaces\/[a-f0-9]+$/);
  const id = page.url().split('/').at(-1)!;
  const asking = await waitForWorkspace(request, id, value => value.clarification !== null);
  expect(asking.agents.filter(agent => agent.assigned)).toHaveLength(1);
  await page.getByRole('button', { name: 'Focus questions', exact: true }).click();
  const slider = page.getByRole('slider');
  await expect(slider).toHaveValue('3');
  await slider.press('ArrowRight');
  await expect(slider).toHaveValue('4');
  await page.getByRole('checkbox', { name: 'Leadership', exact: true }).check();
  await expect(page.getByRole('region', { name: 'Your selections', exact: true })).toContainText('Leadership');
  await expect(page.getByRole('button', { name: 'Continue', exact: true })).toBeInViewport();
  await expect(page.getByRole('switch')).toBeHidden();
  await page.getByText('Optional refinements', { exact: true }).click();
  await page.getByRole('switch').check();
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  const produced = await waitForWorkspace(request, id, value => value.artifacts.length > 0);
  expect(produced.agents.filter(agent => agent.assigned).length).toBeGreaterThan(1);
  expect(produced.agents.filter(agent => agent.assigned).length).toBeLessThanOrEqual(5);
  const anchorId = textElement(produced);
  await focusView(page);
  await expect(page.getByRole('table')).toBeVisible();
  await page.locator(`[data-element-id="${anchorId}"]`).first().click();
  await page.getByRole('button', { name: 'Give direction', exact: true }).click();
  const first = 'Make this section more concise without losing the objective.';
  const second = 'Keep the audience explicit and label all assumptions.';
  for (const direction of [first, second]) {
    if (await page.getByRole('dialog').count() === 0) {
      await page.locator(`[data-element-id="${anchorId}"]`).first().click();
      await page.getByRole('button', { name: 'Give direction', exact: true }).click();
    }
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Your direction', { exact: true }).fill(direction);
    const queued = page.waitForResponse(response =>
      response.url().endsWith(`/api/workspaces/${id}/feedback`) && response.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Send direction', exact: true }).click();
    expect((await queued).ok()).toBeTruthy();
  }
  const enqueued = await workspace(request, id);
  expect(enqueued.feedback.map(note => note.text)).toEqual([first, second]);
  expect(enqueued.feedback.every(note => note.revision === 1 && note.elementId !== null)).toBe(true);
  expect(enqueued.jobs.some(job => job.status === 'queued' || job.status === 'running')).toBe(true);
  await page.keyboard.press('Escape');
  const revised = await waitForWorkspace(request, id,
    value => value.jobs.filter(job => job.kind === 'feedback' && job.status === 'completed').length === 2);
  expect(revised.feedback[0].status).toBe('completed');
  expect(revised.artifacts[0].revisions).toHaveLength(3);
  await page.getByRole('button', { name: 'Latest', exact: true }).click();
  await page.getByRole('button', { name: 'Accept version', exact: true }).click();
  await waitForWorkspace(request, id, value => value.artifacts[0].acceptedRevision === 3);
  await page.locator(`[data-element-id="${anchorId}"]`).first().click();
  await page.getByRole('button', { name: 'Edit text', exact: true }).click();
  const humanText = 'Human decision: launch the smallest useful version, then review the evidence.';
  await page.getByRole('dialog').getByLabel('Your revised text', { exact: true }).fill(humanText);
  await page.getByRole('dialog').getByRole('button', { name: 'Save version', exact: true }).click();
  const edited = await waitForWorkspace(request, id, value => value.artifacts[0].acceptedRevision === 4);
  expect(edited.artifacts[0].revisions.at(-1)?.source).toBe('human');
  await page.keyboard.press('Escape');
  await page.reload();
  await focusView(page);
  await expect(page.getByText(humanText, { exact: true })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(errors).toEqual([]);
  await page.screenshot({ path: test.info().outputPath('human-reviewed-workspace.png'), fullPage: true });

  await page.getByRole('button', { name: 'New task', exact: true }).click();
  await page.getByRole('button', { name: 'Try a demo workspace', exact: true }).click();
  await page.waitForURL(url => url.hash.startsWith('#/workspaces/') && !url.hash.endsWith(id));
  const otherId = page.url().split('/').at(-1)!;
  await page.getByRole('slider').waitFor();
  expect((await workspace(request, otherId)).artifacts).toHaveLength(0);
  expect((await workspace(request, id)).artifacts[0].acceptedRevision).toBe(4);
});

test('narrow screens keep artifact text readable and feedback within the viewport', async ({ page, request }) => {
  const value = await demoFixture(request, 'Browser responsive verification: prepare a short reviewable task plan.');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.goto(`/#/workspaces/${value.id}`);
  await focusView(page);
  const paragraph = page.locator(`[data-element-id="${textElement(value)}"]`).first();
  await paragraph.scrollIntoViewIfNeeded();
  await paragraph.click();
  await page.getByRole('button', { name: 'Give direction', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  const box = await dialog.boundingBox();
  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(391);
  expect(box!.y + box!.height).toBeLessThanOrEqual(845);
  await dialog.getByLabel('Your direction', { exact: true }).fill('Preserve this unsent mobile draft.');
  await page.screenshot({ path: test.info().outputPath('mobile-anchored-feedback.png'), fullPage: true });
  await page.keyboard.press('Escape');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('review rejection and visible cancel/retry controls preserve human decisions', async ({ page, request }) => {
  const value = await demoFixture(request, 'Browser control verification: prepare a small, reviewable working plan.');
  await page.goto(`/#/workspaces/${value.id}`);
  await focusView(page);
  await page.getByRole('button', { name: 'Reject', exact: true }).click();
  const rejected = await waitForWorkspace(request, value.id,
    current => current.artifacts[0].revisions[0].status === 'rejected');
  expect(rejected.artifacts[0].acceptedRevision).toBeNull();

  await page.getByRole('textbox', { name: 'Direction for the whole workspace', exact: true })
    .fill('Prepare a simpler alternative that keeps the rejected version in history.');
  const queued = page.waitForResponse(response =>
    response.url().endsWith(`/api/workspaces/${value.id}/feedback`) && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Send workspace direction', exact: true }).click();
  expect((await queued).ok()).toBeTruthy();
  await page.getByRole('button', { name: 'Activity', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await waitForWorkspace(request, value.id, current => current.jobs.at(-1)?.status === 'cancelled');
  expect((await workspace(request, value.id)).artifacts[0].revisions).toHaveLength(1);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  const retried = await waitForWorkspace(request, value.id,
    current => current.jobs.at(-1)?.kind === 'feedback' && current.jobs.at(-1)?.status === 'completed');
  expect(retried.jobs.filter(job => job.status === 'cancelled')).toHaveLength(1);
  expect(retried.artifacts[0].revisions[0].status).toBe('rejected');
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('spatial canvas preserves dragged positions and keeps feedback unscaled after fitting', async ({ page, request }) => {
  const value = await demoFixture(request, 'Spatial verification: prepare a concise decision-ready task brief.');
  await page.goto(`/#/workspaces/${value.id}`);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const header = page.getByRole('heading', { name: 'Objective & context', exact: true });
  const box = await header.boundingBox();
  expect(box).not.toBeNull();
  await page.mouse.move(box!.x + box!.width / 2, box!.y + box!.height / 2);
  await page.mouse.down();
  await page.mouse.move(box!.x + box!.width / 2 + 64, box!.y + box!.height / 2 + 32, { steps: 8 });
  await page.mouse.up();
  const moved = await waitForWorkspace(request, value.id, current =>
    current.layout.positions.context.x !== value.layout.positions.context.x ||
    current.layout.positions.context.y !== value.layout.positions.context.y);
  await page.reload();
  await page.getByRole('button', { name: 'Fit', exact: true }).waitFor();
  expect((await workspace(request, value.id)).layout.positions.context).toEqual(moved.layout.positions.context);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.locator(`[data-element-id="${textElement(value)}"]`).first().click();
  await page.getByRole('button', { name: 'Give direction', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  const scale = await dialog.evaluate(element => element.getBoundingClientRect().width / (element as HTMLElement).offsetWidth);
  expect(scale).toBeGreaterThan(0.98);
  expect(scale).toBeLessThan(1.02);
  const dialogBox = await dialog.boundingBox();
  expect(dialogBox!.x).toBeGreaterThanOrEqual(0);
  expect(dialogBox!.x + dialogBox!.width).toBeLessThanOrEqual(1441);
  await dialog.getByLabel('Your direction', { exact: true }).fill('A direction anchored on the spatial canvas.');
  await page.screenshot({ path: test.info().outputPath('spatial-anchored-feedback.png'), fullPage: true });
});

test('a simple complete task has no forced specialists, questions, or invented AI review', async ({ page, request }) => {
  const value = await demoFixture(request,
    'Write one friendly sentence thanking Jordan for reviewing a prototype. No research or independent review is needed.');
  expect(value.agents.filter(agent => agent.assigned).map(agent => agent.id)).toEqual(['planner']);
  expect(value.clarification).toBeNull();
  expect(value.artifacts[0].revisions.at(-1)?.review).toBeNull();
  await page.goto(`/#/workspaces/${value.id}`);
  await focusView(page);
  await expect(page.getByRole('article', { name: 'Coordinator agent', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Agents 1', exact: true })).toBeVisible();
  await expect(page.getByRole('form', { name: 'Clarification questions', exact: true })).toHaveCount(0);
  await expect(page.getByText('AI review', { exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Accept version', exact: true })).toBeVisible();
});

test('question regeneration is explicit and drafts survive while a new visual form is created', async ({ page, request }) => {
  const value = await demoFixture(request, 'Help me plan an internal product launch.', true);
  expect(value.clarification).not.toBeNull();
  await page.goto(`/#/workspaces/${value.id}`);
  await page.getByRole('button', { name: 'Focus questions', exact: true }).click();
  const slider = page.getByRole('slider');
  await slider.press('End');
  await expect(slider).toHaveValue('5');
  await page.getByRole('checkbox', { name: 'Leadership', exact: true }).check();
  await page.reload();
  await page.getByRole('button', { name: 'Focus questions', exact: true }).click();
  await expect(page.getByRole('slider')).toHaveValue('5');
  await expect(page.getByRole('checkbox', { name: 'Leadership', exact: true })).toBeChecked();
  const originalKey = `workspaces:v1:${value.id}:clarify-${value.clarificationId}`;
  const savedDraft = await page.evaluate(key => localStorage.getItem(key), originalKey);
  expect(savedDraft).not.toBeNull();
  const refreshed = page.waitForResponse(response =>
    response.url().endsWith(`/api/workspaces/${value.id}/clarification/refresh`) &&
    response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Regenerate questions', exact: true }).click();
  expect((await refreshed).ok()).toBeTruthy();
  const current = await waitForWorkspace(request, value.id,
    state => state.clarificationId !== value.clarificationId && state.jobs.at(-1)?.status === 'completed');
  expect(current.jobs.at(-1)?.kind).toBe('clarify');
  expect(current.agents.filter(agent => agent.assigned)).toHaveLength(1);
  await expect(page.getByRole('slider')).toBeVisible();
  expect(await page.evaluate(key => localStorage.getItem(key), originalKey)).toBe(savedDraft);
  await expect(page.getByRole('alert')).toHaveCount(0);
});
