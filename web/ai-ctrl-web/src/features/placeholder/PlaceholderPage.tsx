import { Link, useParams } from 'react-router-dom'
import { Construction, FolderOpen, ShieldOff, Stethoscope } from 'lucide-react'
import { useMachine } from '../../api/queries'
import { Badge } from '../../components/Badge'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import styles from './PlaceholderPage.module.css'

type PlaceholderKind = 'files' | 'vpn' | 'diagnostics'

const content = {
  files: {
    eyebrow: 'Workspace',
    title: '파일',
    description: '머신 워크스페이스의 파일 탐색, 업로드, 다운로드를 제공할 예정입니다.',
    icon: FolderOpen,
    planned: [
      '워크스페이스 목록과 허용 경로 표시',
      '디렉토리 탐색과 파일 메타데이터 조회',
      '업로드·다운로드·mkdir·delete 권한 적용',
      'denyPatterns에 따른 경로 차단',
    ],
    missing: 'M4 서버에는 파일 목록/내용 API가 아직 없습니다.',
  },
  vpn: {
    eyebrow: 'Etc',
    title: 'VPN',
    description: '머신의 VPN 연결 상태와 국가 변경을 제어할 예정입니다.',
    icon: ShieldOff,
    planned: [
      'ProtonVPN 연결 상태와 현재 국가 표시',
      '국가 목록 조회와 연결/해제 명령',
      '관측 IP·국가와 VPN 상태 교차 확인',
      '오프라인·권한 오류 처리',
    ],
    missing: 'M4의 MachineInfo.vpn은 현재 null이며 VPN 명령도 없습니다.',
  },
  diagnostics: {
    eyebrow: 'Etc',
    title: '진단',
    description: '서버, 머신, 인스턴스, 네트워크 상태를 한 화면에서 점검할 예정입니다.',
    icon: Stethoscope,
    planned: [
      'Client 연결과 마지막 heartbeat 확인',
      '인스턴스 health와 capability 점검',
      '서버·머신 시계 오차와 리소스 사용량',
      '연결 테스트와 trace 기반 문제 추적',
    ],
    missing: '종합 진단 API와 실시간 연결 상태 API가 아직 없습니다.',
  },
} as const

export function PlaceholderPage({ kind }: { kind: PlaceholderKind }) {
  const { machineId } = useParams<{ machineId: string }>()
  const machine = useMachine(machineId)
  const page = content[kind]
  const Icon = page.icon

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow={page.eyebrow}
        title={page.title}
        description={page.description}
        meta={
          <>
            <Badge tone="warn">준비 중</Badge>
            {machine.data ? <FreshnessBadge meta={machine.data.meta} /> : null}
          </>
        }
        actions={
          <Link
            to={`/m/${encodeURIComponent(machineId ?? '')}`}
            className={styles.backLink}
          >
            ← 에이전트 목록
          </Link>
        }
      />

      <div className={styles.hero}>
        <span className={styles.icon} aria-hidden="true">
          <Icon size={22} />
        </span>
        <div>
          <p className={styles.heroTitle}>API가 아직 연결되지 않았습니다</p>
          <p className={styles.heroText}>{page.missing}</p>
        </div>
      </div>

      <section className={styles.panel} aria-labelledby="planned-title">
        <div className={styles.panelHeader}>
          <Construction size={15} aria-hidden="true" />
          <h2 id="planned-title" className={styles.panelTitle}>
            예정된 기능
          </h2>
        </div>
        <ul className={styles.list}>
          {page.planned.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </section>

      <InlineNotice tone="info" title="이 화면은 의도적으로 비활성 상태입니다">
        M4에 계약이 추가되면 API 계층과 이 화면을 함께 연결합니다. 현재는 가짜 데이터를
        표시하지 않습니다.
      </InlineNotice>
    </div>
  )
}
