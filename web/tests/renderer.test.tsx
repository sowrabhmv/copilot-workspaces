import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { GeneratedDocument } from '../src/components/GeneratedDocument';
import { ContextCard } from '../src/components/ContextCard';
import { ArtifactCard } from '../src/components/ArtifactCard';
import { CopilotMark, Icon } from '../src/components/Icons';
import { artifactSpec, clarificationSpec, workspace } from './fixtures';

describe('catalog components and human controls', () => {
  it('renders genuine JSON Render components without making an AI recommendation an approval', () => {
    render(<GeneratedDocument candidate={artifactSpec()} kind="artifact" />);
    expect(screen.getByRole('heading', { name: 'A thoughtful launch' })).toBeTruthy();
    expect(screen.getByRole('table', { name: 'Accountabilities' })).toBeTruthy();
    expect(screen.getByText('AI recommendation')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /accept/i })).toBeNull();
  });
  it('escapes strings and rejects generated effects before rendering', () => {
    const spec = artifactSpec();
    spec.elements.body = { type: 'Text', props: { anchorId: 'body', text: '<img src=x onerror="alert(1)">' }, children: [] };
    const view = render(<GeneratedDocument candidate={spec} kind="artifact" />);
    expect(view.container.querySelector('img')).toBeNull();
    expect(screen.getByText('<img src=x onerror="alert(1)">')).toBeTruthy();
    view.rerender(<GeneratedDocument candidate={{ ...spec, state: { approved: true } }} kind="artifact" />);
    expect(screen.getByRole('alert').textContent).toContain('cannot be displayed safely');
    expect(screen.queryByRole('table')).toBeNull();
  });
  it('retains human clarification drafts across canonical refreshes and validates Continue', async () => {
    const doc = { ...workspace(), status: 'needsInput' as const, clarificationId: 'clarify-1', clarification: clarificationSpec() };
    const onAnswers = vi.fn().mockResolvedValue({ ok: true });
    const view = render(<ContextCard workspace={doc} onAnswers={onAnswers} onRegenerate={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(onAnswers).not.toHaveBeenCalled();
    expect(screen.getAllByText('Please answer this question.')).toHaveLength(2);
    fireEvent.click(screen.getByRole('radio', { name: 'Project team' }));
    fireEvent.change(screen.getByRole('textbox', { name: /What constraints matter/ }), { target: { value: 'Two weeks and a small budget.' } });
    view.rerender(<ContextCard workspace={{ ...doc, eventSequence: 11, clarification: structuredClone(doc.clarification) }} onAnswers={onAnswers} onRegenerate={vi.fn()} />);
    expect((screen.getByRole('textbox', { name: /What constraints matter/ }) as HTMLTextAreaElement).value).toBe('Two weeks and a small budget.');
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await waitFor(() => expect(onAnswers).toHaveBeenCalledWith('clarify-1', {
      audience: 'team', constraints: 'Two weeks and a small budget.',
    }));
  });
  it('selects a stable block and exposes host feedback and text editing', () => {
    const onSelect = vi.fn(); const onFeedback = vi.fn(); const onEdit = vi.fn();
    const spec = artifactSpec();
    const view = render(<GeneratedDocument candidate={spec} kind="artifact" onSelect={onSelect} onFeedback={onFeedback} onEdit={onEdit} />);
    fireEvent.click(screen.getByText('Keep the first release focused on a clear customer problem.'));
    expect(onSelect.mock.calls[0][0].element.props.anchorId).toBe('body');
    view.rerender(<GeneratedDocument candidate={spec} kind="artifact" selectedId="body" onSelect={onSelect} onFeedback={onFeedback} onEdit={onEdit} />);
    fireEvent.click(screen.getByRole('button', { name: 'Give direction' }));
    fireEvent.click(screen.getByRole('button', { name: 'Edit text' }));
    expect(onFeedback.mock.calls[0][0].element.props.anchorId).toBe('body');
    expect(onEdit.mock.calls[0][0].element.type).toBe('Text');
  });
  it('keeps accepted/history controls and prevents conflicted acceptance', () => {
    const artifact = workspace().artifacts[0];
    const changed = {
      ...artifact, currentRevision: 2, acceptedRevision: 1,
      revisions: [
        { ...artifact.revisions[0], status: 'accepted' as const },
        { ...artifact.revisions[0], revision: 2, baseRevision: 1, conflict: true },
      ],
    };
    const onRevision = vi.fn();
    render(<ArtifactCard artifact={changed} selected={null} selectedRevision={undefined}
      onRevision={onRevision} onSelect={vi.fn()} onCompose={vi.fn()} onReview={vi.fn()} onFocus={vi.fn()} />);
    expect((screen.getByRole('button', { name: 'Accept version' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Changes need reconciliation.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Accepted' }));
    expect(onRevision).toHaveBeenCalledWith(1);
    expect(screen.getByText('Not human approval')).toBeTruthy();
  });
  it('uses actual sized Fluent glyphs and preserves Copilot artwork geometry', () => {
    const view = render(<><Icon name="context" /><CopilotMark size={32} /></>);
    const svg = view.container.querySelector('svg');
    expect(svg?.getAttribute('viewBox')).toBe('0 0 20 20');
    expect(svg?.style.width).toBe('20px');
    const image = view.container.querySelector('img');
    expect(image?.getAttribute('src')).toBe('/assets/copilot-32.svg');
    expect(image?.style.width).toBe('30.0005px');
    expect(image?.getAttribute('alt')).toBe('');
  });
  it('does not allow approval of an unreadable or invalid candidate', () => {
    const artifact = workspace().artifacts[0];
    const invalid = { ...artifact, revisions: [{ ...artifact.revisions[0], spec: { root: 'broken', elements: {} } }] };
    render(<ArtifactCard artifact={invalid} selected={null} selectedRevision={undefined}
      onRevision={vi.fn()} onSelect={vi.fn()} onCompose={vi.fn()} onReview={vi.fn()} onFocus={vi.fn()} />);
    expect(screen.getByRole('alert').textContent).toContain('cannot be displayed safely');
    expect((screen.getByRole('button', { name: 'Accept version' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
