// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

import * as d3 from 'd3';
import {
  sankey as d3Sankey,
  sankeyLinkHorizontal,
  sankeyCenter,
  SankeyLayout
} from 'd3-sankey';
import { TxPool, ILink, INode } from './types';
import { SankeyNode, SankeyLink } from './sankeyTypes';

export class TxPoolFlow {
  private svg: d3.Selection<SVGSVGElement, unknown, HTMLElement, any>;
  private rectG: d3.Selection<SVGGElement, unknown, HTMLElement, any>;
  private linkG: d3.Selection<SVGGElement, unknown, HTMLElement, any>;
  private nodeG: d3.Selection<SVGGElement, unknown, HTMLElement, any>;

  private sankeyGenerator: SankeyLayout<SankeyNode, SankeyLink>;
  private width = window.innerWidth;
  private height = 250;
  private defs: d3.Selection<SVGGElement, unknown, HTMLElement, any>;

  private blueColors = [
    '#E1F5FE', '#B3E5FC', '#81D4FA', '#4FC3F7',
    '#29B6F6', '#03A9F4', '#039BE5', '#0288D1',
    '#0277BD', '#01579B'
  ];

  private orangeColors = [
    '#FFF5e1', '#FFE0B2', '#FFCC80', '#FFB74D',
    '#FFA726', '#FF9800', '#FB8C00', '#F57C00',
    '#EF6C00', '#E65100'
  ];

  constructor(container: string) {
    this.svg = d3.select(container)
      .append('svg')
      .attr('width', window.innerWidth)
      .attr('height', this.height)
      .attr('viewBox', [0, 0, window.innerWidth, this.height])
      .style('max-width', '100%')
      .style('height', 'auto');
    this.defs = this.svg.append('defs');
    // Prepare gradients
    let colors = this.blueColors.slice(5, -1);
    colors = [...colors, ...colors, ...colors, ...colors];

    this.initGradient('blue-flow', colors);

    // High-level groups
    this.rectG = this.svg.append('g').attr('stroke', '#000');
    this.linkG = this.svg.append('g').attr('fill', 'none').style('mix-blend-mode', 'normal');
    this.nodeG = this.svg.append('g');

    // Sankey layout
    this.sankeyGenerator = d3Sankey<SankeyNode, SankeyLink>()
      .nodeId((n) => n.name)
      .nodeAlign(sankeyCenter)
      .nodeWidth(10)
      .nodePadding(30)
      .nodeSort((a, b) => {
        if (a.inclusion && b.inclusion) {
          return a.name < b.name ? -1 : 1;
        }
        if (a.inclusion) return -1;
        if (b.inclusion) return 1;
        return a.name < b.name ? 1 : -1;
      })
      .linkSort((a, b) => {
        if (a.target.inclusion && b.target.inclusion) {
          return a.source.name < b.source.name ? -1 : 1;
        }
        if (a.target.inclusion) return -1;
        if (b.target.inclusion) return 1;
        return a.target.name < b.target.name ? 1 : -1;
      })
      .extent([[100, 20], [window.innerWidth - 100, this.height - 25]]);
  }

  private initGradient(name: string, colors: string[]): void {
    const flow = this.defs.append('linearGradient')
      .attr('id', name)
      .attr('x1', '0%')
      .attr('y1', '0%')
      .attr('x2', '100%')
      .attr('y2', '0')
      .attr('spreadMethod', 'reflect')
      .attr('gradientUnits', 'userSpaceOnUse');

    flow.selectAll('stop')
      .data(colors)
      .enter()
      .append('stop')
      .attr('offset', (_, i) => i / (colors.length - 1))
      .attr('stop-color', (d) => d);

    flow.append('animate')
      .attr('attributeName', 'x1')
      .attr('values', '0%;200%')
      .attr('dur', '12s')
      .attr('repeatCount', 'indefinite');

    flow.append('animate')
      .attr('attributeName', 'x2')
      .attr('values', '100%;300%')
      .attr('dur', '12s')
      .attr('repeatCount', 'indefinite');
  }

  private isRightAligned(d: SankeyNode): boolean {
    return !d.inclusion;
  }

  /**
   * Update the Sankey diagram.
   */
  public update(txPoolNodes: INode[], data: TxPool): void {
    this.sankeyGenerator.extent([[100, 20], [window.innerWidth - 100, this.height - 25]]);
    // Filter out zero-value links
    const filteredLinks: ILink[] = [];
    const usedNodes: Record<string, boolean> = {};

    this.width = window.innerWidth - (40 + 16);
    this.svg
      .attr('width', this.width)
      .attr('height', this.height)
      .attr('viewBox', [0, 0, this.width, this.height]);

    for (const link of data.links) {
      if (link.value > 0) {
        filteredLinks.push(link);
        usedNodes[link.source] = true;
        usedNodes[link.target] = true;
      }
    }

    const filteredNodes = txPoolNodes.filter((n) => usedNodes[n.name]);

    // Build sankey input
    const sankeyData = {
      nodes: filteredNodes.map((n) => ({ ...n })),
      links: filteredLinks.map((l) => ({ ...l }))
    };

    // D3 sankey modifies sankeyData in-place, but also returns typed arrays
    const { nodes, links } = this.sankeyGenerator(sankeyData) as { nodes: SankeyNode[], links: SankeyLink[]};

    // ====== Rectangles for nodes ======
    this.rectG
      .selectAll<SVGRectElement, SankeyNode>('rect')
      .data(nodes, (d) => d.name)
      .join('rect')
      .attr('x', (d) => d.x0)
      .attr('y', (d) => d.y0)
      .attr('height', (d) => d.y1 - d.y0)
      .attr('width', (d) => d.x1 - d.x0)
      .attr('fill', (d) => {
        if (d.name === 'P2P Network') {
          d.value = data.hashesReceived;
        }
        if (d.inclusion) {
          if (d.name === 'Tx Pool' || d.name === 'Added To Block') {
            return '#FFA726';
          }
          return '#00BFF2';
        }
        return '#555';
      });

    // ====== Paths for links ======
    this.linkG
      .selectAll<SVGPathElement, SankeyLink>('path')
      .data(links, (d) => d.index!) // d.index assigned by sankey
      .join('path')
      .attr('d', sankeyLinkHorizontal())
      .attr('stroke', (d) => (d.target.inclusion ? 'url(#blue-flow)' : '#333'))
      .attr('stroke-width', (d) => Math.max(1, d.width ?? 1));

    // ====== Labels on nodes ======
    // Using the .join(...) pattern
    const textSel = this.nodeG
      .selectAll<SVGTextElement, SankeyNode>('text')
      .data(nodes, (d) => d.name)
      .join(
        // ENTER
        (enter) => enter
          .append('text')
          .attr('data-last', '0'), // initialize
        // UPDATE
        (update) => update,
        // EXIT
        (exit) => exit.remove()
      );

    textSel
      .attr('data-last', function (d) {
        // If there's an old data-current, preserve it; else '0'
        const oldCurrent = d3.select(this).attr('data-current');
        return oldCurrent || '0';
      })
      .attr('data-current', (d) => {
        // Summation of target links if you prefer, or just d.value
        const targetSum = (d.targetLinks || []).reduce((acc, l) => acc + (l.value || 0), 0);
        // Example: whichever is nonzero
        return targetSum || d.value || 0;
      })
      .attr('x', (d) => (this.isRightAligned(d) ? d.x1 + 6 : d.x0 - 6))
      .attr('y', (d) => (d.y0 + d.y1) / 2)
      .attr('dy', '-0.5em')
      .attr('text-anchor', (d) => (this.isRightAligned(d) ? 'start' : 'end'))
      .text((d) => d.name)
      // Now add a <tspan> for the numeric value
      .each(function () {
        // 'this' is the <text> element
        d3.select(this)
          .selectAll<SVGTSpanElement, unknown>('tspan.number')
          .data([0]) // ensure exactly one <tspan>
          .join('tspan')
          .attr('class', 'number')
          .attr('x', () => {
            const nodeData = d3.select(this).datum() as SankeyNode;
            return nodeData && nodeData.inclusion
              ? nodeData.x1 + 6
              : nodeData.x0 - 6;
          })
          .attr('dy', '1em');
      });

    // Transition & tween for the numeric part
    textSel.selectAll<SVGTSpanElement, unknown>('tspan.number')
      .transition()
      .duration(500)
      .tween('text', function () {
        // The parent <text> has the data-last / data-current
        const tspan = d3.select(this);
        const parentText = d3.select(this.parentNode as SVGTextElement);
        const currentValue = parentText.empty() ? 0 : parseFloat(parentText.attr('data-last') || '0');
        const targetValue = parentText.empty() ? 0 : parseFloat(parentText.attr('data-current') || '0');

        const interp = d3.interpolateNumber(currentValue, targetValue);
        return function (t) {
          tspan.text(d3.format(',.0f')(interp(t)));
        };
      });
  }
}
