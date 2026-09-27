const report = JSON.parse(document.getElementById('report-data').textContent);
const palette = ['#185adb', '#088579', '#8b5cc7', '#d16b18', '#d14d74', '#334c73'];
const parseDate = d3.utcParse('%Y-%m-%d');
const series = report.series.flatMap((s, i) => {
  const base = {...s, color: palette[i % palette.length],
    lender: s.sourceUrl.includes('americafirst') ? 'America First' : 'City Creek',
    metric: 'rate', values: s.weeks.map(p => ({...p, date: parseDate(p.weekEnding)}))};
  return s.ratePlusPointsWeeks ? [base, {...base, metric: 'rate + points',
    values: s.ratePlusPointsWeeks.map(p => ({...p, date: parseDate(p.weekEnding)}))}] : [base];
});
const label = s => `${s.lender} · ${s.product.replace(' Year Fixed',' yr')} · ${s.metric}`;
const dash = s => s.metric === 'rate + points' ? '2 5' : s.lender === 'City Creek' ? '7 4' : null;
const width = 1120, height = 430, margin = {top: 24, right: 26, bottom: 48, left: 65};
const svg = d3.select('#chart').append('svg').attr('viewBox', `0 0 ${width} ${height}`)
  .attr('role', 'img').attr('aria-label', 'Weekly average mortgage interest rates and America First interest rate plus discount points over the past year');
const start = parseDate(report.from), end = parseDate(report.series[0]?.weeks.at(-1)?.weekEnding ?? report.asOf);
const x = d3.scaleUtc().domain([start, end]).range([margin.left, width - margin.right]);
const allValues = series.flatMap(s => s.values.filter(p => p.average !== null).map(p => p.average));
const extent = d3.extent(allValues), pad = Math.max(.12, ((extent[1] ?? 1) - (extent[0] ?? 0)) * .18);
const y = d3.scaleLinear().domain([Math.max(0, (extent[0] ?? 0) - pad), (extent[1] ?? 1) + pad]).nice()
  .range([height - margin.bottom, margin.top]);
svg.append('g').attr('transform', `translate(${margin.left},0)`)
  .call(d3.axisLeft(y).ticks(6).tickSize(-(width-margin.left-margin.right)).tickFormat(d => d.toFixed(2)+'%'))
  .call(g => g.select('.domain').remove())
  .call(g => g.selectAll('.tick line').attr('stroke','#e4e9f0'))
  .call(g => g.selectAll('text').attr('fill','#64748b').attr('font-size',12).attr('dx',-8));
svg.append('g').attr('transform', `translate(0,${height-margin.bottom})`)
  .call(d3.axisBottom(x).ticks(d3.utcMonth.every(2)).tickFormat(d3.utcFormat('%b %Y')).tickSize(0).tickPadding(16))
  .call(g => g.select('.domain').remove())
  .call(g => g.selectAll('text').attr('fill','#64748b').attr('font-size',12));
const line = d3.line().defined(p => p.average !== null).x(p => x(p.date)).y(p => y(p.average));
const groups = svg.selectAll('.series').data(series).join('g').attr('class','series');
groups.append('path').attr('class','rate-line').attr('fill','none').attr('stroke',s=>s.color)
  .attr('stroke-width',2.6).attr('stroke-linecap','round').attr('stroke-dasharray',dash)
  .attr('d',s=>line(s.values));
groups.each(function(s) {
  d3.select(this).selectAll('circle').data(s.values.filter(p=>p.average!==null)).join('circle')
    .attr('cx',p=>x(p.date)).attr('cy',p=>y(p.average)).attr('r',2).attr('fill',s.color);
});
const hidden = new Set();
const legend = d3.select('#legend').selectAll('button').data(series).join('button')
  .attr('type','button').attr('aria-pressed','true')
  .on('click', function(event,s) {
    hidden.has(s) ? hidden.delete(s) : hidden.add(s);
    d3.select(this).attr('aria-pressed',!hidden.has(s)).style('opacity',hidden.has(s)?.4:1);
    groups.style('display',s=>hidden.has(s)?'none':null);
  });
legend.append('span').attr('class','swatch').style('border-color',s=>s.color)
  .style('border-top-style',s=>s.metric==='rate + points'?'dotted':s.lender==='City Creek'?'dashed':'solid');
legend.append('span').text(label);
if (!allValues.length) svg.append('text').attr('x',width/2).attr('y',height/2).attr('text-anchor','middle')
  .attr('fill','#64748b').text('No saved observations in this period.');
const cursor = svg.append('line').attr('stroke','#94a3b8').attr('stroke-dasharray','3 4')
  .attr('y1',margin.top).attr('y2',height-margin.bottom).style('display','none');
const tooltip = d3.select('#tooltip');
svg.append('rect').attr('x',margin.left).attr('y',margin.top)
  .attr('width',width-margin.left-margin.right).attr('height',height-margin.top-margin.bottom)
  .attr('fill','transparent').on('pointermove', function(event) {
    if(!series.length) return;
    const pointer = d3.pointer(event,this), date = x.invert(pointer[0]);
    const index = d3.bisector(p=>p.date).center(series[0].values,date), point=series[0].values[index];
    cursor.style('display',null).attr('x1',x(point.date)).attr('x2',x(point.date));
    tooltip.style('display','block').selectAll('*').remove();
    tooltip.append('strong').text(`Week ending ${d3.utcFormat('%b %d, %Y')(point.date)}`);
    for (const s of series.filter(s=>!hidden.has(s))) {
      const p=s.values[index];
      tooltip.append('div').text(`${label(s)}: ${p.average===null?'No observations':p.average.toFixed(3)+'% · '+p.observations+' day(s)'}`);
    }
  }).on('pointerleave',()=>{cursor.style('display','none');tooltip.style('display','none');});
window.__chartReady = true;
