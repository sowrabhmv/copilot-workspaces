import { Component, StrictMode, type ErrorInfo, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import './styles.css';

class ApplicationBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() { return { failed: true }; }
  componentDidCatch(error: Error, info: ErrorInfo) { console.error('Workspace UI error', error, info.componentStack); }
  render() {
    if (this.state.failed) return <main className="application-error" role="alert">
      <h1>The workspace could not be displayed.</h1>
      <p>Your saved work is still in the local service. Browser drafts are kept when storage is available.</p>
      <button className="button-primary" onClick={() => window.location.reload()}>Reload workspace</button>
    </main>;
    return this.props.children;
  }
}

const root = document.getElementById('root');
if (!root) throw new Error('The application root is missing.');
createRoot(root).render(<StrictMode><ApplicationBoundary><App /></ApplicationBoundary></StrictMode>);
