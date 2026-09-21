import {
  Add20Regular, ArrowCounterclockwise16Regular, ArrowRight20Regular, ArrowUp20Regular,
  Bot20Regular, CalendarCheckmark20Regular, Checkmark16Regular, CheckmarkCircle20Regular,
  ChevronDown16Regular, ChevronRight16Regular, Cursor20Regular, Dismiss20Regular,
  DocumentBulletList20Regular, DocumentCheckmark20Regular, DocumentText20Regular,
  FullScreenMaximize20Regular, HandLeft20Regular, History20Regular,
  ReOrderDotsVertical16Regular, ShieldCheckmark20Regular, Sparkle20Regular, Stop16Regular,
  Subtract20Regular, Table20Regular, TaskListSquareLtr20Regular,
  type FluentIcon,
} from '@fluentui/react-icons';

const icons = {
  add: [Add20Regular, 20], retry: [ArrowCounterclockwise16Regular, 16],
  arrow: [ArrowRight20Regular, 20], send: [ArrowUp20Regular, 20], agent: [Bot20Regular, 20],
  calendar: [CalendarCheckmark20Regular, 20], check: [Checkmark16Regular, 16],
  approved: [CheckmarkCircle20Regular, 20], down: [ChevronDown16Regular, 16],
  right: [ChevronRight16Regular, 16], cursor: [Cursor20Regular, 20], dismiss: [Dismiss20Regular, 20],
  context: [DocumentBulletList20Regular, 20], review: [DocumentCheckmark20Regular, 20],
  document: [DocumentText20Regular, 20], fit: [FullScreenMaximize20Regular, 20],
  pan: [HandLeft20Regular, 20], history: [History20Regular, 20], drag: [ReOrderDotsVertical16Regular, 16],
  shield: [ShieldCheckmark20Regular, 20], sparkle: [Sparkle20Regular, 20],
  stop: [Stop16Regular, 16], subtract: [Subtract20Regular, 20], table: [Table20Regular, 20],
  tasks: [TaskListSquareLtr20Regular, 20],
} as const satisfies Record<string, readonly [FluentIcon, 16 | 20]>;
export type IconName = keyof typeof icons;

export function Icon({ name, className = '' }: { name: IconName; className?: string }) {
  const [Glyph, size] = icons[name];
  return <Glyph aria-hidden="true" focusable="false" className={`icon ${className}`}
    style={{ width: size, height: size, fontSize: size }} />;
}
export function CopilotMark({ size = 32 }: { size?: 32 | 48 }) {
  const geometry = size === 32
    ? { width: 30.0005, height: 28, left: 0.998, top: 2 }
    : { width: 41.9997, height: 38, left: 3.004, top: 5 };
  return <span className="copilot-mark" aria-hidden="true" style={{ width: size, height: size }}>
    <img src={`${import.meta.env.BASE_URL}assets/copilot-${size}.svg`} alt="" draggable={false}
      style={geometry} />
  </span>;
}
