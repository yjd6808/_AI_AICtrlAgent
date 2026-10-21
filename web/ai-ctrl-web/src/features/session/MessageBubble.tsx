import { Bot, TerminalSquare, User } from 'lucide-react'
import type { ErrorInfo, MessageView, PartView, ToolPartView } from '../../api/types'
import { errorClassLabels } from '../../api/labels'
import { formatAbsolute } from '../../api/format'
import { Badge } from '../../components/Badge'
import { MarkdownView } from '../../components/MarkdownView'
import { cx } from '../../lib/cx'
import styles from './MessageBubble.module.css'

function roleLabel(role: string): string {
  switch (role.toLowerCase()) {
    case 'user':
      return '사용자'
    case 'assistant':
      return '어시스턴트'
    case 'system':
      return '시스템'
    case 'tool':
      return '도구'
    default:
      return role || '메시지'
  }
}

function RoleIcon({ role }: { role: string }) {
  const normalized = role.toLowerCase()
  if (normalized === 'user') {
    return <User size={14} aria-hidden="true" />
  }
  if (normalized === 'assistant') {
    return <Bot size={14} aria-hidden="true" />
  }
  return <TerminalSquare size={14} aria-hidden="true" />
}

export function MessageBubble({ message }: { message: MessageView }) {
  const role = message.role.toLowerCase()
  const isUser = role === 'user'
  const isSystem = role === 'system' || role === 'tool'

  return (
    <article
      className={cx(
        styles.message,
        isUser && styles.user,
        isSystem && styles.system,
        !message.completedAt && styles.incomplete,
      )}
    >
      <div className={styles.metaRow}>
        <span className={styles.role}>
          <RoleIcon role={message.role} />
          {roleLabel(message.role)}
        </span>
        <time dateTime={message.createdAt} title={formatAbsolute(message.createdAt)}>
          {formatAbsolute(message.createdAt)}
        </time>
        {message.model ? <Badge tone="neutral">{message.model.modelId}</Badge> : null}
        {!message.completedAt ? <Badge tone="info">진행 중</Badge> : null}
      </div>

      <div className={styles.body}>
        {message.parts.map((part) => (
          <Part key={part.partId} part={part} role={role} />
        ))}
      </div>

      {message.error ? <MessageError error={message.error} /> : null}
    </article>
  )
}

function Part({ part, role }: { part: PartView; role: string }) {
  if (part.type === 'text' && part.text) {
    return (
      <div className={styles.textPart}>
        <MarkdownView content={part.text} />
      </div>
    )
  }

  if (part.tool) {
    return <ToolPart tool={part.tool} />
  }

  if (part.text) {
    return (
      <pre className={styles.rawPart}>
        <code>{part.text}</code>
      </pre>
    )
  }

  if (role === 'assistant') {
    return <p className={styles.emptyPart}>표시할 내용이 없습니다.</p>
  }

  return null
}

function ToolPart({ tool }: { tool: ToolPartView }) {
  const completed = tool.status.toLowerCase() === 'completed'
  const failed = tool.status.toLowerCase() === 'error' || tool.status.toLowerCase() === 'failed'

  return (
    <details className={styles.tool}>
      <summary className={styles.toolSummary}>
        <TerminalSquare size={14} aria-hidden="true" />
        <span className={styles.toolName}>{tool.tool}</span>
        <span className={styles.toolTitle}>{tool.title ?? '도구 실행'}</span>
        <Badge tone={failed ? 'danger' : completed ? 'ok' : 'info'}>{tool.status}</Badge>
      </summary>
      <div className={styles.toolBody}>
        {tool.input ? (
          <div>
            <p className={styles.toolLabel}>입력</p>
            <pre>
              <code>{tool.input}</code>
            </pre>
          </div>
        ) : null}
        {tool.outputPreview ? (
          <div>
            <p className={styles.toolLabel}>출력</p>
            <pre>
              <code>{tool.outputPreview}</code>
            </pre>
          </div>
        ) : null}
        {!tool.input && !tool.outputPreview ? (
          <p className={styles.emptyPart}>표시할 입출력이 없습니다.</p>
        ) : null}
      </div>
    </details>
  )
}

function MessageError({ error }: { error: ErrorInfo }) {
  return (
    <div className={styles.error}>
      <div className={styles.errorHeader}>
        <span>{errorClassLabels[error.class] ?? error.class}</span>
        <span>{error.name}</span>
        {error.statusCode !== null ? <span>HTTP {error.statusCode}</span> : null}
      </div>
      <p className={styles.errorMessage}>{error.message}</p>
      {error.rawJson ? (
        <details>
          <summary>원본 오류</summary>
          <pre className={styles.rawPart}>
            <code>{error.rawJson}</code>
          </pre>
        </details>
      ) : null}
    </div>
  )
}
