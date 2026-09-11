import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ConditionBadge, CoverageBadge, HandlingBadge, SeverityBadge } from './Badges';

describe('badges carry meaning in text, not only colour (08 §4)', () => {
  it('severity has a label and a data attribute', () => {
    render(<SeverityBadge severity="critical" />);
    expect(screen.getByLabelText('Severity Critical')).toHaveAttribute('data-severity', 'critical');
    expect(screen.getByText('Critical')).toBeInTheDocument();
  });

  it('condition and handling are separate badges', () => {
    render(
      <>
        <ConditionBadge state="firing" />
        <HandlingBadge state="acknowledged" />
      </>,
    );
    expect(screen.getByText('Firing')).toBeInTheDocument();
    expect(screen.getByText('Acknowledged')).toBeInTheDocument();
  });

  it('coverage is silent when healthy and loud otherwise', () => {
    const { container, rerender } = render(<CoverageBadge state="healthy" />);
    expect(container).toBeEmptyDOMElement();
    rerender(<CoverageBadge state="degraded" />);
    expect(screen.getByText('Coverage degraded')).toBeInTheDocument();
  });
});
