import { useState } from 'react';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ActionResult } from '../src/api';
import type { Job, Workspace } from '../src/domain';
import { ContextCard } from '../src/components/ContextCard';
import { GeneratedDocument } from '../src/components/GeneratedDocument';
import { WorkspaceScreen } from '../src/components/WorkspaceScreen';
import { seedAnswers, type WireSpec } from '../src/ui-schema';
import { deferred, richClarificationSpec, visualClarificationSpec, workspace } from './fixtures';

function document(spec = visualClarificationSpec(), clarificationId = 'clarify-visual'): Workspace {
  return { ...workspace(), status: 'needsInput', clarification: spec, clarificationId };
}
function controls(spec: WireSpec) {
  function Controlled() {
    const [answers, setAnswers] = useState(() => seedAnswers(spec));
    return <GeneratedDocument candidate={spec} kind="clarification" formId="controlled"
      answers={answers} onAnswer={(id, value) => setAnswers((previous) => ({ ...previous, [id]: value }))} />;
  }
  return render(<Controlled />);
}
function refreshJob(status: Job['status']): Job {
  return { ...workspace().jobs[0], id: 'refresh-1', kind: 'clarify', order: 2, status, message: 'Updating the questions.' };
}

describe('meaningful declarative clarification controls', () => {
  it('renders the mock-style labelled slider and stakeholder chips, not a kitchen-sink text form', () => {
    const submit = vi.fn<() => Promise<ActionResult>>().mockResolvedValue({ ok: true });
    const view = render(<ContextCard workspace={document()} onAnswers={submit} onRegenerate={vi.fn()} />);
    const slider = screen.getByRole('slider', { name: 'How detailed should the plan be?' }) as HTMLInputElement;
    expect(slider.value).toBe('3');
    expect(slider.min).toBe('1');
    expect(slider.max).toBe('5');
    expect(slider.step).toBe('1');
    expect(slider.getAttribute('aria-valuetext')).toBe('Standard Plan');
    expect(screen.getByText('Executive Brief')).toBeTruthy();
    expect(screen.getByText('Full Playbook')).toBeTruthy();
    expect(screen.getAllByRole('checkbox')).toHaveLength(4);
    expect((screen.getByRole('checkbox', { name: 'Product' }) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByRole('checkbox', { name: 'Marketing' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getAllByText('Suggested default')).toHaveLength(2);
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.queryByRole('spinbutton')).toBeNull();
    expect(screen.queryByRole('switch')).toBeNull();
    expect(view.container.querySelector('input[type="date"]')).toBeNull();
    expect(view.container.querySelector<HTMLDetailsElement>('.objective-recap')?.open).toBe(false);
    expect(submit).not.toHaveBeenCalled();
  });
  it('confirms the visible defaults as strings only after Continue', async () => {
    const submit = vi.fn().mockResolvedValue({ ok: true });
    render(<ContextCard workspace={document()} onAnswers={submit} onRegenerate={vi.fn()} />);
    expect(submit).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith('clarify-visual', {
      detail: '3', stakeholders: '["product","marketing"]',
    }));
  });
  it('supports slider keyboard steps and keeps its live human-readable summary synchronized', async () => {
    const submit = vi.fn().mockResolvedValue({ ok: true });
    render(<ContextCard workspace={document()} onAnswers={submit} onRegenerate={vi.fn()} />);
    const slider = screen.getByRole('slider', { name: 'How detailed should the plan be?' }) as HTMLInputElement;
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    expect(slider.value).toBe('4');
    expect(slider.getAttribute('aria-valuetext')).toBe('Detailed Plan');
    const summary = screen.getByRole('region', { name: 'Your selections' });
    expect(within(summary).getByText('Detailed Plan')).toBeTruthy();
    fireEvent.keyDown(slider, { key: 'End' });
    expect(slider.value).toBe('5');
    fireEvent.keyDown(slider, { key: 'Home' });
    expect(slider.value).toBe('1');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Marketing' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Sales' }));
    expect(within(summary).getByText('Product, Sales')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith('clarify-visual', {
      detail: '1', stakeholders: '["product","sales"]',
    }));
  });
  it('does not mistake a null slider default for the browser thumb position', async () => {
    const spec = visualClarificationSpec();
    const sliderNode = spec.elements.detail;
    if (sliderNode.type !== 'SliderQuestion') throw new Error('Fixture');
    sliderNode.props.value = null;
    const submit = vi.fn().mockResolvedValue({ ok: true });
    render(<ContextCard workspace={document(spec)} onAnswers={submit} onRegenerate={vi.fn()} />);
    const slider = screen.getByRole('slider');
    expect(slider.getAttribute('aria-valuetext')).toContain('Not selected');
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(submit).not.toHaveBeenCalled();
    expect(screen.getByText('Please answer this question.')).toBeTruthy();
    fireEvent.keyDown(slider, { key: 'Home' });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit.mock.calls[0][1].detail).toBe('1'));
  });
  it('groups optional refinements without hiding primary decisions or deleting their defaults', async () => {
    const submit = vi.fn().mockResolvedValue({ ok: true });
    const view = render(<ContextCard workspace={document(richClarificationSpec())} onAnswers={submit} onRegenerate={vi.fn()} />);
    const root = view.container.querySelector('.clarification-form > .generated-section');
    expect(root?.querySelectorAll(':scope > .generated-question')).toHaveLength(4);
    const optional = view.container.querySelector<HTMLDetailsElement>('.optional-refinements');
    if (!optional) throw new Error('Optional refinements missing');
    expect(optional.open).toBe(false);
    expect(optional.querySelectorAll('.generated-question')).toHaveLength(3);
    expect(screen.getByText('4 decisions to shape the next step. 3 optional refinements.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith('clarify-visual', {
      detail: '3', stakeholders: '["product","marketing"]', tone: 'direct', risks: 'true',
      budget: '5000', deadline: '2026-10-14', notes: '',
    }));
  });
  it('renders numeric, date, switch, choice and open-text semantics through the same registry', async () => {
    const submit = vi.fn().mockResolvedValue({ ok: true });
    const view = render(<ContextCard workspace={document(richClarificationSpec())} onAnswers={submit} onRegenerate={vi.fn()} />);
    const optional = view.container.querySelector<HTMLDetailsElement>('.optional-refinements');
    if (!optional) throw new Error('Optional refinements missing');
    fireEvent.click(within(optional).getByText('Optional refinements'));
    const amount = screen.getByRole('spinbutton', { name: 'Budget ceiling' }) as HTMLInputElement;
    expect(amount.min).toBe('0');
    expect(amount.max).toBe('100000');
    expect(amount.step).toBe('250');
    fireEvent.change(amount, { target: { value: '1.25e4' } });
    const date = screen.getByLabelText('Target launch date') as HTMLInputElement;
    expect(date.type).toBe('date');
    expect(date.min).toBe('2026-10-01');
    expect(date.max).toBe('2026-12-31');
    fireEvent.change(date, { target: { value: '2026-11-02' } });
    fireEvent.click(screen.getByRole('switch', { name: 'Include launch risks?' }));
    fireEvent.click(screen.getByRole('radio', { name: 'Warm and encouraging' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Anything else that changes the plan?' }), { target: { value: 'Keep the pilot small.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit).toHaveBeenCalledWith('clarify-visual', {
      detail: '3', stakeholders: '["product","marketing"]', tone: 'warm', risks: 'false',
      budget: '12500', deadline: '2026-11-02', notes: 'Keep the pilot small.',
    }));
  });
  it('keeps both controls visible when a small form contains only optional preferences', () => {
    const spec = visualClarificationSpec();
    for (const id of ['detail', 'stakeholders']) {
      const node = spec.elements[id];
      if (node.type === 'SliderQuestion' || node.type === 'MultiChoiceQuestion') node.props.required = false;
    }
    const view = controls(spec);
    expect(screen.getByRole('slider')).toBeTruthy();
    expect(screen.getAllByRole('checkbox')).toHaveLength(4);
    expect(view.container.querySelector('.optional-refinements')).toBeNull();
  });
  it('preserves draft values and input focus across canonical updates of the same form', () => {
    const original = document(richClarificationSpec());
    const props = { onAnswers: vi.fn(), onRegenerate: vi.fn() };
    const view = render(<ContextCard workspace={original} {...props} />);
    const slider = screen.getByRole('slider') as HTMLInputElement;
    slider.focus();
    fireEvent.keyDown(slider, { key: 'End' });
    view.rerender(<ContextCard workspace={{ ...original, revision: 6, eventSequence: 11, clarification: structuredClone(original.clarification) }} {...props} />);
    expect(slider.value).toBe('5');
    expect(globalThis.document.activeElement).toBe(slider);
    expect(screen.getByRole('slider')).toBe(slider);
  });
});

describe('explicit question regeneration', () => {
  it('does not regenerate saved forms automatically and submits the current identity on request', async () => {
    const refresh = vi.fn().mockResolvedValue({ ok: true });
    render(<ContextCard workspace={document()} onAnswers={vi.fn()} onRegenerate={refresh} />);
    expect(refresh).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Regenerate questions' }));
    await waitFor(() => expect(refresh).toHaveBeenCalledWith('clarify-visual'));
  });
  it('blocks regeneration during unrelated queued work without mislabelling it as a question refresh', () => {
    const refresh = vi.fn();
    const queued = { ...refreshJob('queued'), kind: 'feedback' as const };
    render(<ContextCard workspace={{ ...document(), jobs: [queued] }} onAnswers={vi.fn()} onRegenerate={refresh} />);
    const button = screen.getByRole('button', { name: 'Regenerate questions' }) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    fireEvent.click(button);
    expect(refresh).not.toHaveBeenCalled();
    expect(screen.queryByText(/Updating questions in the background/)).toBeNull();
  });
  it.each(['queued', 'running', 'cancelling'] as const)('keeps the old form but blocks its submission during a %s refresh', (status) => {
    const submit = vi.fn(); const refresh = vi.fn();
    render(<ContextCard workspace={{ ...document(), jobs: [refreshJob(status)] }} onAnswers={submit} onRegenerate={refresh} />);
    expect(screen.getByRole('slider')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Regenerate questions' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.submit(screen.getByRole('form', { name: 'Clarification questions' }));
    expect(submit).not.toHaveBeenCalled();
    expect(refresh).not.toHaveBeenCalled();
    expect(screen.getByText(/Your previous form and draft stay here/)).toBeTruthy();
  });
  it.each(['failed', 'cancelled', 'interrupted'] as const)('restores old-form usability after a %s refresh without losing edits', async (status) => {
    const original = document();
    const submit = vi.fn().mockResolvedValue({ ok: true });
    const view = render(<ContextCard workspace={original} onAnswers={submit} onRegenerate={vi.fn()} />);
    fireEvent.keyDown(screen.getByRole('slider'), { key: 'End' });
    view.rerender(<ContextCard workspace={{ ...original, jobs: [refreshJob('running')] }} onAnswers={submit} onRegenerate={vi.fn()} />);
    view.rerender(<ContextCard workspace={{ ...original, jobs: [refreshJob(status)] }} onAnswers={submit} onRegenerate={vi.fn()} />);
    expect((screen.getByRole('slider') as HTMLInputElement).value).toBe('5');
    expect((screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.getByText(`Question refresh ${status}. Your previous form and draft are still available.`)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(submit.mock.calls[0][1].detail).toBe('5'));
  });
  it('keeps old draft storage when a successful refresh publishes a new form identity', () => {
    const original = document();
    const view = render(<ContextCard workspace={original} onAnswers={vi.fn()} onRegenerate={vi.fn()} />);
    fireEvent.keyDown(screen.getByRole('slider'), { key: 'End' });
    const oldKey = 'workspaces:v1:workspace-1:clarify-clarify-visual';
    expect(JSON.parse(localStorage.getItem(oldKey) ?? '{}').detail).toBe('5');
    const replacement = document(visualClarificationSpec(), 'clarify-new');
    view.rerender(<ContextCard workspace={replacement} onAnswers={vi.fn()} onRegenerate={vi.fn()} />);
    expect((screen.getByRole('slider') as HTMLInputElement).value).toBe('3');
    expect(JSON.parse(localStorage.getItem(oldKey) ?? '{}').detail).toBe('5');
    expect(JSON.parse(localStorage.getItem('workspaces:v1:workspace-1:clarify-clarify-new') ?? '{}').detail).toBe('3');
  });
  it('blocks concurrent submission during the enqueue request and surfaces a refresh rejection', async () => {
    const request = deferred<ActionResult>();
    const refresh = vi.fn().mockReturnValue(request.promise);
    const submit = vi.fn();
    render(<ContextCard workspace={document()} onAnswers={submit} onRegenerate={refresh} />);
    fireEvent.click(screen.getByRole('button', { name: 'Regenerate questions' }));
    expect((screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.submit(screen.getByRole('form', { name: 'Clarification questions' }));
    expect(submit).not.toHaveBeenCalled();
    await act(async () => request.resolve({ ok: false, message: 'The form changed. Please refresh.', conflict: true }));
    expect(screen.getByRole('alert').textContent).toContain('The form changed');
    expect((screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(false);
  });
  it('offers readable unscaled question focus without replacing the form draft', async () => {
    const initial = document();
    render(<WorkspaceScreen workspace={initial} bootstrap={null} connection="connected" error={null}
      onDocument={vi.fn()} onRefresh={vi.fn()} onReconnect={vi.fn()} onMenu={vi.fn()} onHome={vi.fn()} />);
    const slider = screen.getByRole('slider') as HTMLInputElement;
    fireEvent.keyDown(slider, { key: 'End' });
    fireEvent.click(screen.getByRole('button', { name: 'Focus questions' }));
    await waitFor(() => expect(screen.getByRole('region', { name: 'Focused workspace' })).toBeTruthy());
    expect(globalThis.document.querySelector<HTMLElement>('.canvas-world')?.style.transform).toBe('');
    expect((screen.getByRole('slider') as HTMLInputElement).value).toBe('5');
  });
});
